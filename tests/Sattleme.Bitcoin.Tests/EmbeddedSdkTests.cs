using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sattleme.Bitcoin.Services;
using Sattleme.Bitcoin.Tests.Support;
using SmartstoreSattleme.Sattleme.Checkout;
using SmartstoreSattleme.Sattleme.Checkout.Internal;
using SmartstoreSattleme.Sattleme.Checkout.Internal.Crypto;

namespace Sattleme.Bitcoin.Tests;

/// <summary>
/// The SDK as the module ships it (its sources compiled under the module's names, NBitcoin.Secp256k1
/// with them), against the SDK's own conformance vectors that touch the cryptography: integration
/// keys, BIP-340 signatures, NIP-44 encryption. And the real client, made by the module, without the
/// network.
/// </summary>
[TestFixture]
public sealed class EmbeddedSdkTests
{
    private static byte[] Hex(string hex)
    {
        return Convert.FromHexString(hex);
    }

    private static JsonElement Vectors(params string[] path)
    {
        return JsonDocument.Parse(File.ReadAllText(Repository.File(["sdk", .. path]))).RootElement;
    }

    [Test]
    public void Reads_the_integration_keys_of_the_specification()
    {
        var cases = Vectors("spec", "vectors", "integration-keys.json").GetProperty("cases").EnumerateArray().ToList();
        Assert.That(cases, Has.Count.GreaterThan(10));
        foreach (var vector in cases)
        {
            var name = vector.GetProperty("name").GetString();
            var key = vector.GetProperty("key").ValueKind == JsonValueKind.String ? vector.GetProperty("key").GetString() : null;
            var expected = vector.GetProperty("expected");
            if (expected.TryGetProperty("error", out var error))
            {
                var thrown = Assert.Catch<SattlemeException>(() => IntegrationKey.Parse(key), name ?? "");
                Assert.That(thrown!.Code, Is.EqualTo(error.GetString()), name);
                continue;
            }
            var parsed = IntegrationKey.Parse(key);
            Assert.That(parsed.TeamPublicKey, Is.EqualTo(expected.GetProperty("teamPublicKey").GetString()), name);
            Assert.That(Convert.ToHexStringLower(parsed.Key), Is.EqualTo(expected.GetProperty("integrationKey").GetString()), name);
            Assert.That(parsed.Relays, Is.EqualTo(expected.GetProperty("relays").EnumerateArray().Select(relay => relay.GetString()).ToArray()), name);
        }
    }

    [Test]
    public void Signs_and_verifies_as_BIP_340_says()
    {
        var rows = File.ReadAllLines(Repository.File("sdk", "tests", "Sattleme.Checkout.Tests", "Vectors", "bip340", "test-vectors.csv"))
            .Skip(1).Where(line => line.Length > 0).Select(line => line.Split(',')).ToList();
        Assert.That(rows, Has.Count.EqualTo(19));
        // An event's id, the only message the SDK signs or verifies, is 32 bytes: the vectors of other lengths are left out, as the SDK's own tests do.
        var cases = rows.Where(row => row[4].Length == 64).ToList();
        Assert.That(cases, Has.Count.EqualTo(15));
        foreach (var row in cases)
        {
            var (secret, publicKey, aux, message, signature, valid) = (row[1].ToLowerInvariant(), row[2].ToLowerInvariant(), row[3].ToLowerInvariant(), row[4].ToLowerInvariant(), row[5].ToLowerInvariant(), row[6] == "TRUE");
            if (secret.Length > 0)
            {
                Assert.That(Secp256k1.PublicKeyOf(Hex(secret)), Is.EqualTo(publicKey), row[0]);
                Assert.That(Convert.ToHexStringLower(Secp256k1.Sign(Hex(message), Hex(secret), Hex(aux))), Is.EqualTo(signature), row[0]);
            }
            Assert.That(Secp256k1.Verify(signature, message, publicKey), Is.EqualTo(valid), row[0]);
        }
    }

    [Test]
    public void Encrypts_and_decrypts_as_NIP_44_says()
    {
        // The app's copy of the official vectors (github.com/paulmillr/nip44), as the SDK's own tests read it.
        var vectors = Vectors("spec", "vectors", "nip44", "nip44.vectors.json");
        var count = 0;
        foreach (var vector in vectors.GetProperty("get_conversation_key").EnumerateArray())
        {
            Assert.That(Convert.ToHexStringLower(Nip44.ConversationKey(Hex(vector.GetProperty("sec1").GetString()!), vector.GetProperty("pub2").GetString()!)),
                Is.EqualTo(vector.GetProperty("conversation_key").GetString()));
            count++;
        }
        foreach (var vector in vectors.GetProperty("get_message_keys").GetProperty("keys").EnumerateArray())
        {
            var (chachaKey, chachaNonce, hmacKey) = Nip44.MessageKeys(Hex(vectors.GetProperty("get_message_keys").GetProperty("conversation_key").GetString()!), Hex(vector.GetProperty("nonce").GetString()!));
            Assert.That(Convert.ToHexStringLower(chachaKey), Is.EqualTo(vector.GetProperty("chacha_key").GetString()));
            Assert.That(Convert.ToHexStringLower(chachaNonce), Is.EqualTo(vector.GetProperty("chacha_nonce").GetString()));
            Assert.That(Convert.ToHexStringLower(hmacKey), Is.EqualTo(vector.GetProperty("hmac_key").GetString()));
            count++;
        }
        foreach (var pair in vectors.GetProperty("calc_padded_len").EnumerateArray())
        {
            Assert.That(Nip44.PaddedLength(pair[0].GetInt32()), Is.EqualTo(pair[1].GetInt32()));
            count++;
        }
        foreach (var vector in vectors.GetProperty("encrypt_decrypt").EnumerateArray())
        {
            var key = Hex(vector.GetProperty("conversation_key").GetString()!);
            Assert.That(Nip44.EncryptWithNonce(vector.GetProperty("plaintext").GetString()!, key, Hex(vector.GetProperty("nonce").GetString()!)), Is.EqualTo(vector.GetProperty("payload").GetString()));
            Assert.That(Nip44.Decrypt(vector.GetProperty("payload").GetString()!, key), Is.EqualTo(vector.GetProperty("plaintext").GetString()));
            count++;
        }
        foreach (var vector in vectors.GetProperty("invalid_get_conversation_key").EnumerateArray())
        {
            Assert.Catch<ArgumentException>(() => Nip44.ConversationKey(Hex(vector.GetProperty("sec1").GetString()!), vector.GetProperty("pub2").GetString()!), vector.GetProperty("note").GetString() ?? "");
            count++;
        }
        foreach (var vector in vectors.GetProperty("invalid_decrypt").EnumerateArray())
        {
            var error = Assert.Catch(() => Nip44.Decrypt(vector.GetProperty("payload").GetString()!, Hex(vector.GetProperty("conversation_key").GetString()!)));
            Assert.That(error, Is.InstanceOf<FormatException>().Or.InstanceOf<UnknownNip44VersionException>(), vector.GetProperty("note").GetString());
            count++;
        }
        Assert.That(count, Is.GreaterThan(100));
    }

    [Test]
    public void The_real_client_refuses_what_is_not_an_integration_key()
    {
        var gateway = new SattlemeGateway(NullLoggerFactory.Instance);

        Assert.Throws<InvalidKeyException>(() => gateway.ClientOf(""));
        Assert.Throws<InvalidKeyException>(() => gateway.ClientOf("sattleme_ck_damaged"));
        Assert.Throws<InvalidKeyException>(() => gateway.ClientOf(SdkData.KEY.Replace("qEVej", "qEVe", StringComparison.Ordinal)));
    }

    [Test]
    public void The_real_client_is_made_once_per_key_without_the_network_and_never_shows_the_key()
    {
        var gateway = new SattlemeGateway(NullLoggerFactory.Instance);
        var client = gateway.ClientOf(SdkData.KEY);

        Assert.That(gateway.ClientOf(SdkData.KEY), Is.SameAs(client));
        Assert.That(client.ToString(), Does.Not.Contain(SdkData.KEY).And.Not.Contain(SdkData.KEY[12..40]));
    }

    [Test]
    public void The_SDK_s_log_goes_to_Smartstore_s_log_under_the_module_s_name()
    {
        // Smartstore's ILoggerFactory (Serilog): its database log keeps warnings and errors.
        var factory = new CapturingLoggerFactory();
        var gateway = new SattlemeGateway(factory);
        gateway.ClientOf(SdkData.KEY);

        Assert.That(factory.Categories, Is.EqualTo(new[] { "Sattleme.Bitcoin.Sdk" }));
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Categories { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            return NullLogger.Instance;
        }

        public void Dispose()
        {
        }
    }
}
