using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace CSharpCodeGraph
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            builder.RootComponents.Add<App>("#app");
            builder.RootComponents.Add<HeadOutlet>("head::after");
            // For loading GitHub repositories (Diagram/GitHubRepository.cs).
            builder.Services.AddScoped(_ => new HttpClient());

            await builder.Build().RunAsync();
        }
    }
}
