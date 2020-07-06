namespace Centrifuge.Client
{
    class CommandRecord
    {
        public bool IsResponseReceived { get; set; }
        public Method Method { get; set; }
        public object Parameters { get; set; }
        public int Id { get; set; }
    }
}