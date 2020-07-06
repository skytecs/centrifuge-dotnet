namespace Centrifuge.Client
{
    class ConnectResult
    {
        public string Client { get; set; }
        public int TTL { get; set; }
        public bool Expires { get; set; }
        public string Version { get; set; }
    }

}
