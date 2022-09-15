using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifuge.Client
{
    public class CentrifugeClient : IDisposable
    {
        private int _id = 0;
        private bool _disposedValue;

        private ClientWebSocket _socket = new ClientWebSocket();
        private readonly ConcurrentDictionary<int, CommandRecord> _commands = new ConcurrentDictionary<int, CommandRecord>();
        private readonly ConcurrentDictionary<string, IIncomingMessageHandler> _handlers = new ConcurrentDictionary<string, IIncomingMessageHandler>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private Task _connectTask;
        private Task _receiveTask;
        private readonly Uri _url;
        private readonly Func<string> _tokenGenerator;

        public event EventHandler<ExceptionEventArgs> ConnectionInterrupted;
        public event EventHandler<EventArgs> ConnectionEstablished;
        public event EventHandler<ExceptionEventArgs> FormatError;

        public CentrifugeClient(Uri url, Func<string> tokenGenerator)
        {
            _url = url;
            _tokenGenerator = tokenGenerator;
        }

        public CentrifugeClient(string url, Func<string> tokenGenerator) : this(new Uri(url), tokenGenerator)
        {
        }

        public async Task Listen()
        {
            if (_socket.State != WebSocketState.None)
            {
                _socket.Dispose();
                _socket = new ClientWebSocket();
            }

            try
            {
                await _socket.ConnectAsync(_url, CancellationToken.None);
            }
            catch (Exception e)
            {
                OnConnectionInterrupted(e);
                return;
            }

            _connectTask = SendAsync(Method.Connect, new
            {
                token = _tokenGenerator()
            });

            await _connectTask;

            // process pending subscriptions

            foreach (var channel in _handlers.Keys)
            {
                await SendAsync(Method.Subscribe, new
                {
                    channel
                });
            }

            var _ = Task.Run(() => DoPing(_cancellationTokenSource.Token));

            _receiveTask = DoReceive(_cancellationTokenSource.Token);

            OnConnectionEstablished();


            await _receiveTask;
        }

        public async Task Subscribe<TData>(string channel, Action<TData> callback)
        {
            if (_connectTask != null && _connectTask.IsCompleted)
            {
                await SendAsync(Method.Subscribe, new
                {
                    channel
                });
            }

            _handlers.TryAdd(channel, new IncomingMessageHandler<TData> { Callback = callback });
        }

        private async Task SendAsync(Method method, object @params)
        {
            var connect = new
            {
                id = ++_id,
                method,
                @params
            };

            await Task.Run(() => _commands.TryAdd(_id,
                new CommandRecord {IsResponseReceived = false, Id = _id, Method = method, Parameters = @params}));


            if (_socket.State == WebSocketState.Open)
            {
                await _socket.SendAsync(CommandToBinary(connect), WebSocketMessageType.Text, true,
                    CancellationToken.None);
            }
            else
            {
                OnConnectionInterrupted(new ApplicationException($"Socket wasn't open when sending {JsonConvert.SerializeObject(connect)}"));
            }
        }

        private ArraySegment<byte> CommandToBinary(object command) => new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(command)));

        private async Task DoReceive(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using (var buffer = new MemoryStream())
                {
                    while (true)
                    {
                        var frame = new ArraySegment<byte>(new byte[8192]);
                        var result = await _socket.ReceiveAsync(frame, CancellationToken.None);

                        await buffer.WriteAsync(frame.Array, 0, result.Count, cancellationToken);

                        if (!result.EndOfMessage)
                        {
                            continue;
                        }

                        buffer.Position = 0;

                        try
                        {
                            await Process(buffer);
                        }
                        catch (Exception e)
                        {
                            OnConnectionInterrupted(e);
                            return;
                        }

                        if (result.CloseStatus != null)
                        {
                            OnConnectionInterrupted(new ApplicationException($"WebSocket had CloseStatus {result.CloseStatus}"));
                            return;
                        }

                        if (_socket.State != WebSocketState.Open)
                        {
                            OnConnectionInterrupted(new ApplicationException($"WebSocket had State {_socket.State}"));
                            return;
                        }

                        break;
                    }
                }
            }
        }

        private async Task Process(Stream stream)
        {
            using (var streamReader = new StreamReader(stream))
            using (var jsonReader = new JsonTextReader(streamReader))
            {
                var message = await JObject.LoadAsync(jsonReader);

                Console.WriteLine(message.ToString());

                var errorProperty = message.Property("error");

                if (errorProperty != null && errorProperty.HasValues)
                {
                    await Task.Run(() => Accept(errorProperty.Value.ToObject<Error>()));
                }

                var idProperty = message.Property("id");
                // That's a response for the request in the history
                if (idProperty != null && idProperty.HasValues)
                {
                    var id = idProperty.Value.Value<int>();

                    if (_commands.ContainsKey(id))
                    {
                        switch (_commands[id].Method)
                        {
                            case Method.Connect:
                            case Method.Refresh:

                                var result = message.Property("result")?.Value.ToObject<ConnectResult>();

                                _commands[id].IsResponseReceived = true;

                                Accept(result);

                                break;
                        }
                    }
                }
                else
                {
                    var result = message.GetValue("result")?.ToObject<IncomingResult>();

                    if (result != null)
                    {
                        Accept(result);
                    }
                    else
                    {
                        OnFormatError(new ArgumentNullException(nameof(result)));
                    }
                }
            }
        }

        private void Accept(IncomingResult result)
        {
            _handlers[result.Channel].Call(result.Data);
        }

        private void Accept(Error error)
        {
            OnFormatError(new Exception($"Centrifuge error: {error?.Code}-{error?.Message}"));

        }

        private void Accept(ConnectResult result)
        {
            // We are sending refresh requests 5 seconds before expiration
            var ttl = Math.Max(result.TTL - 5, 5);

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            Task.Run(async () =>
            {
                await Task.Delay(ttl * 1000);

                await SendAsync(Method.Refresh, new
                {
                    token = _tokenGenerator()
                });
            });
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        }


        private async Task DoPing(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                await Task.Delay(15000, cancellationToken);

                await SendAsync(Method.Ping, null);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                    _cancellationTokenSource.Cancel();
                    _socket.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                _disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~CentrifugeClient()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void OnConnectionInterrupted(Exception e)
        {
            ConnectionInterrupted?.Invoke(this, new ExceptionEventArgs(e));
        }

        protected virtual void OnFormatError(Exception e)
        {
            FormatError?.Invoke(this, new ExceptionEventArgs(e));
        }

        protected virtual void OnConnectionEstablished()
        {
            ConnectionEstablished?.Invoke(this, EventArgs.Empty);
        }
    }

    public class ExceptionEventArgs
    {
        public ExceptionEventArgs(Exception e)
        {
            Exception = e;
        }
        public Exception Exception { get; }
    }
}
