using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AURA.Profile.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace AURA.BackupRecovery.Checks;

public class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("====================================================================");
        Console.WriteLine("AURA Task SCRUM-340 (W4-M1-03) Automated Verification Suite");
        Console.WriteLine("====================================================================");

        var repoRoot = FindRepoRoot();
        Console.WriteLine($"Repo Root: {repoRoot}");

        Console.WriteLine("Backup integration gate: pwsh scripts/backup/Test-BackupRecovery.ps1 -Container <isolated PostgreSQL>");
        Console.WriteLine("This executable checks RSA rotation only; it does not certify disaster recovery.");
        await TestAC3_ZeroDowntimeDualKeyRotationAsync();

        Console.WriteLine();
        Console.WriteLine("====================================================================");
        Console.WriteLine($"RESULTS: Passed: {_passed}, Failed: {_failed}");
        Console.WriteLine("====================================================================");

        return _failed == 0 ? 0 : 1;
    }

    private static async Task TestAC3_ZeroDowntimeDualKeyRotationAsync()
    {
        Console.WriteLine("\n[AC3] Verifying Zero-Downtime Dual-Key Rotation Buffer in ServiceTokenValidator:");

        // 1. Generate RSA Key Pair 1 (Active / Primary key)
        using var rsaCurrent = RSA.Create(2048);
        var currentPublicKeyPem = rsaCurrent.ExportSubjectPublicKeyInfoPem();
        var currentPrivateKey = new RsaSecurityKey(rsaCurrent);

        // 2. Generate RSA Key Pair 2 (Previous / Secondary key)
        using var rsaPrevious = RSA.Create(2048);
        var previousPublicKeyPem = rsaPrevious.ExportSubjectPublicKeyInfoPem();
        var previousPrivateKey = new RsaSecurityKey(rsaPrevious);

        // 3. Generate RSA Key Pair 3 (Untrusted / Foreign key)
        using var rsaUntrusted = RSA.Create(2048);
        var untrustedPrivateKey = new RsaSecurityKey(rsaUntrusted);

        var issuer = "aura-identity";
        var audience = "aura-service";
        var scope = "analysis.read";

        // Setup ServiceTokenValidator with current + previous key in rotation buffer
        var configDict = new Dictionary<string, string?>
        {
            ["ServiceAuth:Issuer"] = issuer,
            ["ServiceAuth:Audience"] = audience,
            ["ServiceAuth:PublicKeyPem"] = currentPublicKeyPem,
            ["ServiceAuth:PreviousPublicKeyPem"] = previousPublicKeyPem
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        var validator = new ServiceTokenValidator(configuration, NullLogger<ServiceTokenValidator>.Instance);

        // Helper to create token signed with specific RSA key
        string CreateToken(RSA rsa, SecurityKey signingKey)
        {
            var keyId = "aura-local-" + Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()[..16];
            var tokenHandler = new JwtSecurityTokenHandler();
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim("sub", Guid.NewGuid().ToString()),
                    new Claim("client_id", "aura-worker-batch"),
                    new Claim("scope", scope)
                }),
                Expires = DateTime.UtcNow.AddMinutes(15),
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
            };
            var token = tokenHandler.CreateJwtSecurityToken(tokenDescriptor);
            token.Header["kid"] = keyId;
            return tokenHandler.WriteToken(token);
        }

        // Test Token signed with Current Key
        var tokenCurrent = CreateToken(rsaCurrent, currentPrivateKey);
        var resultCurrent = await validator.ValidateTokenAsync(tokenCurrent, audience, scope);
        Assert(resultCurrent.Valid, "AC3.1 Token signed with Primary Key validates successfully");

        // Test Token signed with Previous Key (Worker in-flight during rotation window)
        var tokenPrevious = CreateToken(rsaPrevious, previousPrivateKey);
        var resultPrevious = await validator.ValidateTokenAsync(tokenPrevious, audience, scope);
        Assert(resultPrevious.Valid, "AC3.2 Token signed with Previous Key validates successfully (Zero 401 Downtime)");

        // Test Token signed with Untrusted Key
        var tokenUntrusted = CreateToken(rsaUntrusted, untrustedPrivateKey);
        var resultUntrusted = await validator.ValidateTokenAsync(tokenUntrusted, audience, scope);
        Assert(!resultUntrusted.Valid && resultUntrusted.ErrorCode == "ERR_UNAUTHORIZED", "AC3.3 Untrusted key is strictly rejected with ERR_UNAUTHORIZED");
    }

    private static void Assert(bool condition, string testName)
    {
        if (condition)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [PASS] {testName}");
            Console.ResetColor();
            _passed++;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [FAIL] {testName}");
            Console.ResetColor();
            _failed++;
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AURA.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
