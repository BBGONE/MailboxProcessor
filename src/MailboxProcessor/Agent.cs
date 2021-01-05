namespace MailboxProcessor
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public static class Agent
    {
        public static Agent<T> Start<T>(Func<Agent<T>, Task> body, CancellationToken? cancellationToken = null, int? capacity = null)
            where T : class
        {
            var agent = new Agent<T>(body, cancellationToken, capacity);
            agent.Start();
            return agent;
        }
    }

    public class Agent<TMsg> : IDisposable
    {
        private readonly Func<Agent<TMsg>, Task> _body;
        private readonly Mailbox<TMsg> _mailbox;
        private readonly Observable<Exception> _errorEvent;
        private volatile int _started;
        private Task _agentTask;

        public Agent(Func<Agent<TMsg>, Task> body, CancellationToken? cancellationToken = null, int? capacity = null)
        {
            _body = body;
            _mailbox = new Mailbox<TMsg>(cancellationToken, capacity);
            DefaultTimeout = Timeout.Infinite;
            _errorEvent = new Observable<Exception>();
            _started = 0;
        }

        public IObservable<Exception> Errors => _errorEvent;

        public bool IsRunning => _started == 1 && !this.CancellationToken.IsCancellationRequested;

        public int DefaultTimeout { get; set; }

        public CancellationToken CancellationToken => _mailbox.CancellationToken;


        public void Start()
        {
            int oldStarted = Interlocked.CompareExchange(ref _started, 1, 0);

            if (oldStarted == 1)
                throw new InvalidOperationException("MailboxProcessor already started");

            async Task StartAsync()
            {
                try
                {
                   await _body(this);
                }
                catch (Exception exception)
                {
                    // var err = ExceptionDispatchInfo.Capture(exception);
                    _errorEvent.OnNext(exception);
                    throw;
                }
            }

            this._agentTask = Task.Factory.StartNew(StartAsync, this.CancellationToken, TaskCreationOptions.None, TaskScheduler.Default).Unwrap();
            this._agentTask.ContinueWith((antecedent) => {
                var error = antecedent.Exception;
                try
                {
                    if (error != null)
                    {
                        this.ReportError(error);
                    }
                }
                finally
                {
                    // proceed anyway (error or not - clean up anyway)
                    Interlocked.CompareExchange(ref _started, 0, 1);
                    Interlocked.CompareExchange(ref _agentTask, null, _agentTask);
                }
            });
        }

        public async Task Stop ()
        {
            int oldStarted = Interlocked.CompareExchange(ref _started, 0, 1);
            if (oldStarted == 1)
            {
                try
                {
                    var savedTask = _agentTask ?? Task.CompletedTask;
                    _mailbox.Stop();
                    await savedTask;
                }
                catch (OperationCanceledException)
                {
                    // NOOP
                }
            }
        }

        public async Task Post(TMsg message)
        {
            try
            {
                await _mailbox.Post(message);
            }
            catch (AggregateException ex)
            {
                Exception firstError = null;
                ex.Flatten().Handle((err) =>
                {
                    firstError = firstError ?? err;
                    return true;
                });

                // Channel was closed
                if (!this.IsRunning)
                {
                    throw new OperationCanceledException(this.CancellationToken);
                }
                else
                {
                    throw firstError;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Channel was closed
                if (!this.IsRunning)
                {
                    throw new OperationCanceledException(this.CancellationToken);
                }
                else
                {
                    throw;
                }
            }
        }

        public async Task<TReply> PostAndReply<TReply>(Func<IReplyChannel<TReply>, TMsg> msgf, int? timeout = null)
        {
            timeout = timeout ?? DefaultTimeout;
            var tcs = new TaskCompletionSource<TReply>(TaskCreationOptions.RunContinuationsAsynchronously);

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(this.CancellationToken))
            {
                if (timeout.Value != Timeout.Infinite)
                {
                    cts.CancelAfter(timeout.Value);
                }

                using (cts.Token.Register(() => tcs.TrySetCanceled(this.CancellationToken), useSynchronizationContext: false))
                {
                    var msg = msgf(new ReplyChannel<TReply>(reply =>
                    {
                        tcs.TrySetResult(reply);
                    }));

                    await this.Post(msg);

                    return await tcs.Task;
                }
            }
        }

        public async Task<TMsg> Receive()
        {
            try
            {
                return await _mailbox.Receive();
            }
            catch (AggregateException ex)
            {
                Exception firstError = null;
                ex.Flatten().Handle((err) =>
                {
                    firstError = firstError ?? err;
                    return true;
                });

                // Channel was closed
                if (!this.IsRunning)
                {
                    throw new OperationCanceledException(this.CancellationToken);
                }
                else
                {
                    throw firstError;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Channel was closed
                if (!this.IsRunning)
                {
                    throw new OperationCanceledException(this.CancellationToken);
                }
                else
                {
                    throw;
                }
            }
        }

        public bool TryReceive(out TMsg msg)
        {
            return _mailbox.TryReceive(out msg);
        }

        public void ReportError(Exception ex)
        {
            _errorEvent.OnNext(ex);
        }

        public void Dispose()
        {
            this.Stop().Wait(1000);
        }
    }
}
