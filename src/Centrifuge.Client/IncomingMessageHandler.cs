using System;
using System.Threading.Tasks;

namespace Centrifuge.Client
{
    interface IIncomingMessageHandler
    {
        void Call(IncomingData data);
    }

    class IncomingMessageHandler<TData> : IIncomingMessageHandler
    {
        public Action<TData> Callback { get; internal set; }

        public void Call(IncomingData data)
        {
            Task.Run(() =>
            {
                Callback(data.Data.ToObject<TData>());
            });
        }
    }
}