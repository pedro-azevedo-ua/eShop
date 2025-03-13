using System.Diagnostics;

namespace eShop.WebApp
{
    public class Instrumentation : IDisposable
    {
        internal const string ActivitySourceName = "eShop.Basket.API";
        internal const string ActivitySourceVersion = "1.0.0";

        public Instrumentation()
        {
            ActivitySource = new ActivitySource(ActivitySourceName, ActivitySourceVersion);
        }

        public ActivitySource ActivitySource { get; }

        public void Dispose()
        {
            ActivitySource.Dispose();
        }
    }
}
