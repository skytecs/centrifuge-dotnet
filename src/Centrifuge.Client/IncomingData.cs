using Newtonsoft.Json.Linq;

namespace Centrifuge.Client
{
    class IncomingData
    {
        public int Seq { get; set; }
        public JToken Data { get; set; }
    }

}
