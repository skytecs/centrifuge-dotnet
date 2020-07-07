using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Centrifuge.Client.Demo
{
    class Program
    {
        static readonly Guid ClientId = Guid.NewGuid();

        static async Task Main(string[] args)
        {
            var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddUserSecrets<Program>().Build();

            var client = new CentrifugeClient(new Uri(configuration["Url"]), () => GenerateToken(configuration["Secret"]));

            var listen = client.Listen();

            await client.Subscribe<string>("test_channel", Console.WriteLine);

            await listen;
        }

        private static string GenerateToken(string secret)
        {
            var handler = new JwtSecurityTokenHandler();
            var key = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(secret));

            var signingCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var header = new JwtHeader(signingCredentials);

            var payload = new JwtPayload(new[]
            {
                new Claim("sub", ClientId.ToString()),
                new Claim("nbf", DateTimeOffset.UtcNow.AddSeconds(-2).ToUnixTimeSeconds().ToString()),
                new Claim("exp", DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds().ToString()),
                new Claim("iat", DateTimeOffset.UtcNow.AddSeconds(-2).ToUnixTimeSeconds().ToString()),
            });

            var token = new JwtSecurityToken(header, payload);

            return handler.WriteToken(token);
        }

    }
}
