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
        private bool disposedValue;

        private ClientWebSocket _socket = new ClientWebSocket();
        private readonly ConcurrentDictionary<int, CommandRecord> _commands = new ConcurrentDictionary<int, CommandRecord>();
        private readonly ConcurrentDictionary<string, IIncomingMessageHandler> _handlers = new ConcurrentDictionary<string, IIncomingMessageHandler>();
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private Task _pingTask;
        private Task _connectTask;
        private Task _receiveTask;
        private readonly Uri _url;
        private readonly Func<string> _tokenGenerator;

        public event EventHandler<EventArgs> ConnectionInterrupted;


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

            await _socket.ConnectAsync(_url, CancellationToken.None);

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

            _pingTask = Task.Run(() => DoPing(_cancellationTokenSource.Token));

            _receiveTask = Task.Run(() => DoReceive(_cancellationTokenSource.Token));

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
                OnConnectionInterrupted();
            }
        }

        private ArraySegment<byte> CommandToBinary(object command) => new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(command)));

        private async Task DoReceive(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using (var buffer = new MemoryStream())
                {
                    while (true)
                    {
                        var frame = new ArraySegment<byte>(new byte[8192]);
                        var result = await _socket.ReceiveAsync(frame, CancellationToken.None);

                        buffer.Write(frame.Array, 0, result.Count);

                        if (result.EndOfMessage)
                        {
                            buffer.Position = 0;

                            await Process(buffer);

                            if (result.CloseStatus != null)
                            {
                                OnConnectionInterrupted();
                            }

                            break;
                        }
                    }
                }
            }
        }

        private async Task Process(Stream stream)
        {
            using (var streamReader = new StreamReader(stream))
            using (var jsonReader = new JsonTextReader(streamReader))
            {
                var message = JObject.Load(jsonReader);

                Console.WriteLine(message.ToString());

                var errorProperty = message.Property("error");

                if (errorProperty != null && errorProperty.HasValues)
                {
                    await Task.Run(async () => await Accept(errorProperty.Value.ToObject<Error>()));
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

                                var result = message.Property("result").Value.ToObject<ConnectResult>();

                                _commands[id].IsResponseReceived = true;

                                await Accept(result);

                                break;
                        }
                    }
                }
                else
                {
                    var result = message.Property("result").Value.ToObject<IncomingResult>();

                    await Accept(result);
                }
            }
        }

        private async Task Accept(IncomingResult result)
        {
            _handlers[result.Channel].Call(result.Data);
        }

        private async Task Accept(Error error)
        {
        }

        private async Task Accept(ConnectResult result)
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
                await Task.Delay(15000);

                await SendAsync(Method.Ping, null);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                    _cancellationTokenSource.Cancel();
                    _socket.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
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

        protected virtual void OnConnectionInterrupted()
        {
            ConnectionInterrupted?.Invoke(this, EventArgs.Empty);
        }
    }

}
