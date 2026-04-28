using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Sol.Api.Services;

namespace Sol.Api.Diagnostics
{
    public static class JplFieldVerifierRunner
    {
        public static async Task RunAsync(IServiceProvider services)
        {
            var reader = services.GetRequiredService<IAuthoritativeBodyCatalogReader>() as AuthoritativeBodyCatalogReader;
            if (reader == null)
            {
                Console.WriteLine("AuthoritativeBodyCatalogReader not available.");
                return;
            }
            var verifier = new JplFieldVerifier(reader);
            await verifier.VerifyAllBodiesAsync();
        }
    }
}
