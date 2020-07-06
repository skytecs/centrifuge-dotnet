namespace Centrifuge.Client
{
    class SubscribeResult
    {
        public bool Recoverable { get; set; }
        public int Seq { get; set; }
        public string Epoch { get; set; }
        public int Offset { get; set; }
    }

}
