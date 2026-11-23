using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tactile.Core;
using Tactile.Core.Catalog;
using Tactile.Core.Updates;
using Tactile.Host;

namespace Tactile.Tests.Catalog;

[Collection("Localization")]
public class CatalogTests
{
    [Fact]
    public async Task Does_not_touch_network_until_catalog_is_requested()
    {
        using var f = new Fixture();
        f.Publish(Index(1, f.Entry("alpha-run", ProfileBytes(withRun: true))));
        using CatalogService service = f.Service();

        Assert.Equal(CatalogState.Idle, service.Status.State);
        Assert.Empty(service.Status.Profiles);
        Assert.Empty(f.Requests);

        await service.FetchAsync();
        Assert.Equal(CatalogState.Ready, service.Status.State);
        Assert.Equal(2, f.Requests.Count);
    }

    [Fact]
    public async Task Accepts_valid_signed_index_and_imports_profile_with_a_new_id()
    {
        using var f = new Fixture();
        byte[] file = ProfileBytes();
        f.Publish(Index(1, f.Entry("alpha-run", file)), ("profiles/alpha-run.json", file));
        using CatalogService service = f.Service();
        var store = new ProfileStore(f.Dir.Path);
        int before = store.All.Count;

        await service.FetchAsync();
        CatalogEntry entry = Assert.Single(service.Status.Profiles);
        Assert.Equal("alpha-run", entry.Id);
        Assert.Equal("1", File.ReadAllText(f.RevisionFile));

        CatalogImport? imported = await service.ImportAsync("alpha-run", store);
        Assert.NotNull(imported);
        Assert.Equal(0, imported.RemovedRunActions);
        Assert.NotEqual("shared-id", imported.Profile.Id);
        Assert.Equal(32, imported.Profile.Id.Length);
        Profile saved = Assert.IsType<Profile>(store.Get(imported.Profile.Id));
        Assert.Equal(ActionKind.Key, saved.Mapping.Buttons["cross"].Press.Kind);
        Assert.Equal(before + 1, store.All.Count);
        Assert.Equal(CatalogState.Ready, service.Status.State);
        Assert.Equal(3, f.Requests.Count);
    }

    [Fact]
    public async Task Refuses_a_signed_profile_with_a_run_action_and_saves_nothing()
    {
        using var f = new Fixture();
        byte[] file = ProfileBytes(withRun: true);
        f.Publish(Index(1, f.Entry("alpha-run", file)), ("profiles/alpha-run.json", file));
        using CatalogService service = f.Service();
        var store = new ProfileStore(f.Dir.Path);
        int before = store.All.Count;

        await service.FetchAsync();
        Assert.Null(await service.ImportAsync("alpha-run", store));
        Assert.Equal(CatalogError.BadFile, service.Status.Error);
        Assert.Equal(before, store.All.Count);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("signature")]
    [InlineData("missing")]
    public async Task Rejects_tampered_index_or_signature_before_parsing(string what)
    {
        using var f = new Fixture();
        byte[] index = Index(1, f.Entry("alpha-run", ProfileBytes()));
        f.Publish(index);
        switch (what)
        {
            case "index":
                f.Files[Fixture.IndexUrl][10] ^= 1;
                break;
            case "signature":
                byte[] raw = Convert.FromBase64String(Encoding.ASCII.GetString(f.Files[Fixture.SignatureUrl]));
                raw[5] ^= 1;
                f.Files[Fixture.SignatureUrl] = Encoding.ASCII.GetBytes(Convert.ToBase64String(raw));
                break;
            default:
                f.Files.Remove(Fixture.SignatureUrl);
                break;
        }

        using CatalogService service = f.Service();
        await service.FetchAsync();
        Assert.Equal(CatalogState.Error, service.Status.State);
        Assert.Equal(CatalogError.BadSignature, service.Status.Error);
        Assert.Empty(service.Status.Profiles);
    }

    [Fact]
    public async Task Index_signed_by_another_key_is_rejected_even_when_valid()
    {
        using var f = new Fixture();
        f.Publish(Index(1, f.Entry("alpha-run", ProfileBytes())));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var client = new CatalogClient("1.5.0", f.Handler(), other.ExportSubjectPublicKeyInfo());
        CatalogException error = await Assert.ThrowsAsync<CatalogException>(() => client.FetchIndexAsync(default));
        Assert.Equal(CatalogError.BadSignature, error.Code);
    }

    [Theory]
    [InlineData("https://example.com/index.json")]
    [InlineData("http://raw.githubusercontent.com/index.json")]
    [InlineData("https://raw.githubusercontent.com:8443/index.json")]
    [InlineData("https://user:pass@raw.githubusercontent.com/index.json")]
    [InlineData("https://github.com/kaan237/triggerforge-profiles/raw/main/index.json")]
    public async Task Rejects_untrusted_hosts_non_https_and_redirect_targets(string target)
    {
        using var f = new Fixture();
        f.Publish(Index(1, f.Entry("alpha-run", ProfileBytes())));
        f.Override = url => url.AbsoluteUri == Fixture.IndexUrl ? Redirect(target) : null;
        using CatalogClient client = f.Client();
        CatalogException error = await Assert.ThrowsAsync<CatalogException>(() => client.FetchIndexAsync(default));
        Assert.Equal(CatalogError.UntrustedHost, error.Code);
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task Follows_trusted_redirects_but_stops_after_five()
    {
        using var f = new Fixture();
        f.Publish(Index(1, f.Entry("alpha-run", ProfileBytes())));
        int hops = 0;
        f.Override = url => url.AbsoluteUri == Fixture.IndexUrl && hops++ < 5 ? Redirect(Fixture.IndexUrl) : null;
        using (CatalogClient client = f.Client())
        {
            Assert.Single((await client.FetchIndexAsync(default)).Profiles);
        }

        f.Override = url => url.AbsoluteUri == Fixture.IndexUrl ? Redirect(Fixture.IndexUrl) : null;
        using CatalogClient looping = f.Client();
        CatalogException error = await Assert.ThrowsAsync<CatalogException>(() => looping.FetchIndexAsync(default));
        Assert.Equal(CatalogError.Network, error.Code);
    }

    [Theory]
    [InlineData("index", true)]
    [InlineData("index", false)]
    [InlineData("signature", true)]
    [InlineData("signature", false)]
    [InlineData("profile", true)]
    [InlineData("profile", false)]
    public async Task Rejects_oversized_index_signature_and_profile_payloads(string what, bool knownLength)
    {
        using var f = new Fixture();
        byte[] file = ProfileBytes();
        f.Publish(Index(1, f.Entry("alpha-run", file)), ("profiles/alpha-run.json", file));
        int size = what switch { "index" => 512 * 1024 + 1, "signature" => 1025, _ => file.Length + 1 };
        byte[] big = new byte[size];
        Array.Fill(big, (byte)'a');
        string url = what switch { "index" => Fixture.IndexUrl, "signature" => Fixture.SignatureUrl, _ => Fixture.Content + "profiles/alpha-run.json" };
        CatalogEntry entry = CatalogManifest.Parse(f.Files[Fixture.IndexUrl]).Profiles[0];
        f.Override = u => u.AbsoluteUri == url
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = knownLength ? new ByteArrayContent(big) : new UnknownLength(big) }
            : null;
        using CatalogClient client = f.Client();
        Task task = what == "profile" ? client.FetchProfileAsync(entry, default) : client.FetchIndexAsync(default);
        CatalogException error = await Assert.ThrowsAsync<CatalogException>(() => task);
        Assert.Equal(CatalogError.TooLarge, error.Code);
    }

    [Theory]
    [InlineData("../secret.json")]
    [InlineData("profiles\\bad.json")]
    [InlineData("profiles/../secret.json")]
    [InlineData("/profiles/a.json")]
    [InlineData("profiles/A.json")]
    [InlineData("other/a.json")]
    public async Task Rejects_path_traversal_in_signed_index(string path)
    {
        using var f = new Fixture();
        byte[] file = ProfileBytes();
        f.Publish(Index(1, f.Entry("alpha-run", file, path: path)));
        using CatalogService service = f.Service();
        await service.FetchAsync();
        Assert.Equal(CatalogError.BadManifest, service.Status.Error);
        Assert.Empty(service.Status.Profiles);
    }

    [Theory]
    [InlineData("hash", CatalogError.BadFile)]
    [InlineData("shorter", CatalogError.BadFile)]
    [InlineData("longer", CatalogError.TooLarge)]
    [InlineData("run", CatalogError.BadFile)]
    [InlineData("notProfile", CatalogError.BadFile)]
    public async Task Rejects_hash_size_and_content_mismatch_on_profile_download(string what, CatalogError expected)
    {
        using var f = new Fixture();
        byte[] file = ProfileBytes();
        byte[] served = file;
        JsonObject entry = f.Entry("alpha-run", file);
        switch (what)
        {
            case "hash":
                entry["sha256"] = new string('0', 64);
                break;
            case "shorter":
                entry["size"] = file.Length + 1;
                break;
            case "longer":
                entry["size"] = file.Length - 1;
                break;
            case "run":
                // A run action hidden where no model reads it: only the raw check sees it.
                served = Encoding.UTF8.GetBytes("""{"name":"x","extra":[{"kind":"RUN","path":"c:\\a.exe"}]}""");
                entry = f.Entry("alpha-run", served);
                break;
            default:
                served = Encoding.UTF8.GetBytes("[1,2]");
                entry = f.Entry("alpha-run", served);
                break;
        }

        f.Publish(Index(1, entry), ("profiles/alpha-run.json", served));
        using CatalogService service = f.Service();
        var store = new ProfileStore(f.Dir.Path);
        int before = store.All.Count;
        await service.FetchAsync();
        Assert.Null(await service.ImportAsync("alpha-run", store));
        Assert.Equal(expected, service.Status.Error);
        Assert.Equal(CatalogState.Error, service.Status.State);
        Assert.Equal(before, store.All.Count);
        Assert.Single(service.Status.Profiles);
    }

    [Fact]
    public void Share_export_strips_run_actions_and_computes_exact_sha256_and_size()
    {
        Profile profile = JsonSerializer.Deserialize<Profile>(Encoding.UTF8.GetString(ProfileBytes(withRun: true)), TactileJson.Options)!;
        CatalogSharePackage package = CatalogShareExporter.BuildSharePackage(profile with { Name = "Çılgın Şoför — 2" });

        Assert.True(package.RemovedRunActions > 0);
        Assert.False(CatalogProfileFile.ContainsRunAction(Encoding.UTF8.GetBytes(package.Json)));
        Assert.DoesNotContain('\r', package.Json);
        Assert.Equal("cilgin-sofor-2.json", package.FileName);

        JsonObject entry = JsonNode.Parse(package.EntryJson)!.AsObject();
        byte[] bytes = Encoding.UTF8.GetBytes(package.Json);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), (string)entry["sha256"]!);
        Assert.Equal(bytes.Length, (int)entry["size"]!);
        Assert.Equal("profiles/" + package.FileName, (string)entry["path"]!);

        var index = new JsonObject
        {
            ["schema"] = 1, ["revision"] = 1, ["updated"] = "2026-11-22",
            ["profiles"] = new JsonArray(entry),
        };
        Assert.Single(CatalogManifest.Parse(Encoding.UTF8.GetBytes(index.ToJsonString())).Profiles);
    }

    [Theory]
    [InlineData("""{"a":{"kind":"Run"}}""", true)]
    [InlineData("""[{"Kind":"run"}]""", true)]
    [InlineData("""{"x":[{"y":{"kind":" run "}}]}""", true)]
    [InlineData("""{"kind":6}""", true)]
    [InlineData("""{"kind":"6"}""", true)]
    [InlineData("""{"kind":"key","name":"run"}""", false)]
    [InlineData("""{"kind":5}""", false)]
    public void Raw_check_finds_run_actions_anywhere(string json, bool expected) =>
        Assert.Equal(expected, CatalogProfileFile.ContainsRunAction(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Raw_check_rejects_duplicate_properties() =>
        Assert.ThrowsAny<JsonException>(() => CatalogProfileFile.ContainsRunAction("""{"kind":"key","kind":"run"}"""u8));

    [Fact]
    public async Task Sign_and_verify_round_trip_and_client_accepts_the_result()
    {
        using var f = new Fixture();
        using var repo = new TempDir();
        string path = WriteRepo(repo, ProfileBytes());

        CatalogSigner.Sign(repo.Path, f.Key, new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(1, CatalogSigner.Verify(repo.Path, f.Key.ExportSubjectPublicKeyInfo()));
        string text = File.ReadAllText(Path.Combine(repo.Path, "index.json"));
        Assert.DoesNotContain('\r', text);
        Assert.Contains("\"updated\": \"2026-11-22\"", text);
        Assert.Contains("\"revision\": 1", text);

        CatalogSigner.Sign(repo.Path, f.Key, DateTime.UtcNow);
        Assert.Contains("\"revision\": 2", File.ReadAllText(Path.Combine(repo.Path, "index.json")));

        f.Files[Fixture.IndexUrl] = File.ReadAllBytes(Path.Combine(repo.Path, "index.json"));
        f.Files[Fixture.SignatureUrl] = File.ReadAllBytes(Path.Combine(repo.Path, "index.json.sig"));
        f.Files[Fixture.Content + "profiles/alpha-run.json"] = File.ReadAllBytes(path);
        using CatalogService service = f.Service();
        await service.FetchAsync();
        Assert.Equal(CatalogState.Ready, service.Status.State);
        Assert.Equal(2, CatalogManifest.Parse(f.Files[Fixture.IndexUrl]).Revision);
        Assert.NotNull(await service.ImportAsync("alpha-run", new ProfileStore(f.Dir.Path)));
    }

    [Fact]
    public void Sign_refuses_a_profile_with_a_run_action_and_leaves_files_untouched()
    {
        using var f = new Fixture();
        using var repo = new TempDir();
        WriteRepo(repo, ProfileBytes(withRun: true));
        byte[] before = File.ReadAllBytes(Path.Combine(repo.Path, "index.json"));

        Assert.Throws<InvalidDataException>(() => CatalogSigner.Sign(repo.Path, f.Key, DateTime.UtcNow));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(repo.Path, "index.json")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "index.json.sig")));
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("profiles/../../outside.json")]
    [InlineData("profiles\\a.json")]
    [InlineData("C:/outside.json")]
    public void Sign_refuses_paths_that_leave_the_repository(string path)
    {
        using var f = new Fixture();
        using var repo = new TempDir();
        WriteRepo(repo, ProfileBytes(), path);

        Assert.Throws<InvalidDataException>(() => CatalogSigner.Sign(repo.Path, f.Key, DateTime.UtcNow));
    }

    [Fact]
    public void Verify_detects_changed_profile_files_and_wrong_keys()
    {
        using var f = new Fixture();
        using var repo = new TempDir();
        string path = WriteRepo(repo, ProfileBytes());
        CatalogSigner.Sign(repo.Path, f.Key, DateTime.UtcNow);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Throws<InvalidDataException>(() => CatalogSigner.Verify(repo.Path, other.ExportSubjectPublicKeyInfo()));
        File.WriteAllText(path, File.ReadAllText(path).Replace("Alpha", "Alphb"));
        Assert.Throws<InvalidDataException>(() => CatalogSigner.Verify(repo.Path, f.Key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public async Task Refuses_an_older_revision_than_the_last_seen_and_keeps_the_newer_one()
    {
        using var f = new Fixture();
        File.WriteAllText(f.RevisionFile, "5");
        f.Publish(Index(3, f.Entry("alpha-run", ProfileBytes())));
        using CatalogService service = f.Service();

        await service.FetchAsync();
        Assert.Equal(CatalogError.Outdated, service.Status.Error);
        Assert.Empty(service.Status.Profiles);
        Assert.Equal("5", File.ReadAllText(f.RevisionFile));

        f.Publish(Index(5, f.Entry("alpha-run", ProfileBytes())));
        await service.FetchAsync(force: true);
        Assert.Equal(CatalogState.Ready, service.Status.State);

        f.Publish(Index(6, f.Entry("alpha-run", ProfileBytes())));
        await service.FetchAsync(force: true);
        Assert.Equal("6", File.ReadAllText(f.RevisionFile));
    }

    [Fact]
    public async Task A_corrupt_revision_file_counts_as_zero_and_an_unwritable_one_is_ignored()
    {
        using var f = new Fixture();
        File.WriteAllText(f.RevisionFile, "not a number");
        f.Publish(Index(1, f.Entry("alpha-run", ProfileBytes())));
        using (CatalogService service = f.Service())
        {
            await service.FetchAsync();
            Assert.Equal(CatalogState.Ready, service.Status.State);
        }

        // A directory where the file should go makes every write fail.
        string blocked = Path.Combine(f.Dir.Path, "blocked");
        Directory.CreateDirectory(blocked);
        using var client = f.Client();
        using var failing = new CatalogService(client, blocked);
        await failing.FetchAsync();
        Assert.Equal(CatalogState.Ready, failing.Status.State);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("garbage")]
    public async Task Without_a_usable_pinned_key_the_request_fails_before_any_network_access(string key)
    {
        using var f = new Fixture();
        f.Publish(Index(1, f.Entry("alpha-run", ProfileBytes())));
        using var client = new CatalogClient("1.5.0", f.Handler(), key == "empty" ? [] : [1, 2, 3]);
        using var service = new CatalogService(client, f.RevisionFile);

        await service.FetchAsync();
        Assert.Equal(CatalogError.BadSignature, service.Status.Error);
        Assert.Empty(f.Requests);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("duplicate-id")]
    [InlineData("controller")]
    [InlineData("exe")]
    [InlineData("revision")]
    public async Task Rejects_entries_that_break_the_schema(string defect)
    {
        using var f = new Fixture();
        JsonObject entry = f.Entry("alpha-run", ProfileBytes());
        JsonObject[] entries = [entry];
        long revision = 1;
        switch (defect)
        {
            case "summary":
                entry.Remove("summary");
                break;
            case "duplicate-id":
                entries = [entry, f.Entry("alpha-run", ProfileBytes())];
                break;
            case "controller":
                entry["controller"] = "xbox";
                break;
            case "exe":
                entry["games"] = new JsonArray("dir\\game.exe");
                break;
            default:
                revision = 0;
                break;
        }

        f.Publish(Index(revision, entries));
        using CatalogService service = f.Service();
        await service.FetchAsync();
        Assert.Equal(CatalogError.BadManifest, service.Status.Error);
    }

    [Fact]
    public async Task Only_one_operation_runs_at_a_time_and_dispose_cancels_the_running_one()
    {
        using var f = new Fixture();
        f.Publish(Index(1, f.Entry("alpha-run", ProfileBytes())));
        var gate = new TaskCompletionSource();
        f.Hold = gate.Task;
        CatalogService service = f.Service();

        Task first = service.FetchAsync();
        await service.FetchAsync(force: true);
        Assert.Equal(CatalogState.Loading, service.Status.State);

        service.Dispose();
        await first;
        Assert.NotEqual(CatalogState.Ready, service.Status.State);
        gate.TrySetResult();
    }

    [Fact]
    public void Bridge_posts_idle_on_hello_and_ignores_catalog_messages_before_hello_or_from_other_pages()
    {
        using var f = new Fixture();
        f.Publish(Index(1, f.Entry("alpha-run", ProfileBytes())));
        using var dir = new TempDir();
        using AppCore core = CreateCore(dir);
        using var updates = new UpdateService(Paths(dir), new Version(1, 5, 0));
        var messages = new List<string>();
        using CatalogService service = f.Service();
        using var bridge = new Bridge(core, null!, Collect(messages), Paths(dir), updates, service);

        bridge.Handle("""{"type":"catalog.list"}""", appSource: true);
        bridge.Handle("""{"type":"hello"}""", appSource: true);
        JsonElement status = Assert.Single(Of(messages, "catalog.status"));
        Assert.Equal("idle", status.GetProperty("state").GetString());
        Assert.Equal(0, status.GetProperty("profiles").GetArrayLength());
        Assert.Empty(f.Requests);

        foreach (string type in new[] { "catalog.list", "catalog.preview", "catalog.import", "catalog.openRepo", "profile.exportShare" })
        {
            bridge.Handle($$"""{"type":"{{type}}","id":"alpha-run"}""", appSource: false);
        }

        Assert.Empty(f.Requests);
        Assert.Empty(Of(messages, "error"));
    }

    [Fact]
    public async Task Bridge_lists_previews_imports_and_exports_with_the_wire_shape()
    {
        using var f = new Fixture();
        byte[] file = ProfileBytes();
        f.Publish(Index(1, f.Entry("alpha-run", file)), ("profiles/alpha-run.json", file));
        using var dir = new TempDir();
        using AppCore core = CreateCore(dir);
        using var updates = new UpdateService(Paths(dir), new Version(1, 5, 0));
        var messages = new List<string>();
        using CatalogService service = f.Service();
        using var bridge = new Bridge(core, null!, Collect(messages), Paths(dir), updates, service);
        bridge.Handle("""{"type":"hello"}""", appSource: true);

        bridge.Handle("""{"type":"catalog.list"}""", appSource: true);
        JsonElement ready = await WaitFor(messages, "catalog.status", s => s.GetProperty("state").GetString() == "ready");
        JsonElement item = ready.GetProperty("profiles")[0];
        Assert.Equal(
            ["author", "controller", "description", "games", "id", "name", "size", "summary", "tags", "version"],
            item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("edge", item.GetProperty("controller").GetString());
        Assert.Equal("English text", item.GetProperty("description").GetProperty("en").GetString());
        Assert.Equal("off", item.GetProperty("summary").GetProperty("emulation").GetString());
        Assert.Equal(JsonValueKind.Null, ready.GetProperty("error").ValueKind);

        int requests = f.Requests.Count;
        bridge.Handle("""{"type":"catalog.list"}""", appSource: true);
        bridge.Handle("""{"type":"catalog.preview","id":"alpha-run"}""", appSource: true);
        Assert.Equal(requests, f.Requests.Count);
        JsonElement preview = Assert.Single(Of(messages, "catalog.preview"));
        Assert.Equal("alpha-run", preview.GetProperty("id").GetString());
        Assert.Equal("tester", preview.GetProperty("author").GetString());
        Assert.Equal("game.exe", preview.GetProperty("games")[0].GetString());

        bridge.Handle("""{"type":"catalog.preview","id":"nope"}""", appSource: true);
        Assert.Single(Of(messages, "error"));

        bridge.Handle("""{"type":"catalog.import","id":"alpha-run"}""", appSource: true);
        JsonElement notice = await WaitFor(messages, "notice", _ => true);
        Assert.Contains("Alpha", notice.GetProperty("message").GetString());
        Assert.Contains(core.Profiles.All, p => p.Name == "Alpha" && p.Id.Length == 32);

        bridge.Handle($$"""{"type":"profile.exportShare","id":"{{core.ActiveProfile.Id}}"}""", appSource: true);
        JsonElement share = Assert.Single(Of(messages, "exportShare"));
        Assert.EndsWith(".json", share.GetProperty("fileName").GetString());
        Assert.Equal(0, share.GetProperty("removedRunActions").GetInt32());
        Assert.False(string.IsNullOrEmpty(share.GetProperty("entryJson").GetString()));
        bridge.Handle("""{"type":"profile.exportShare","id":"missing"}""", appSource: true);
        Assert.Equal(2, Of(messages, "error").Count);
    }

    [Fact]
    public async Task Bridge_reports_catalog_failures_only_inside_the_status_message()
    {
        using var f = new Fixture();
        f.Publish(Index(1, f.Entry("alpha-run", ProfileBytes())));
        f.Override = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        using var dir = new TempDir();
        using AppCore core = CreateCore(dir);
        using var updates = new UpdateService(Paths(dir), new Version(1, 5, 0));
        var messages = new List<string>();
        using CatalogService service = f.Service();
        using var bridge = new Bridge(core, null!, Collect(messages), Paths(dir), updates, service);
        bridge.Handle("""{"type":"hello"}""", appSource: true);

        bridge.Handle("""{"type":"catalog.list"}""", appSource: true);
        JsonElement failed = await WaitFor(messages, "catalog.status", s => s.GetProperty("state").GetString() == "error");
        Assert.Equal("network", failed.GetProperty("error").GetString());
        Assert.False(string.IsNullOrEmpty(failed.GetProperty("message").GetString()));
        Assert.Empty(Of(messages, "error"));
        Assert.Empty(Of(messages, "notice"));

        f.Override = null;
        bridge.Handle("""{"type":"catalog.list"}""", appSource: true);
        await WaitFor(messages, "catalog.status", s => s.GetProperty("state").GetString() == "ready");
    }

    [Theory]
    [InlineData("badEnum")]
    [InlineData("numberEnum")]
    [InlineData("nullEnum")]
    [InlineData("caseDuplicate")]
    [InlineData("stringCount")]
    [InlineData("numberBool")]
    [InlineData("unknownField")]
    [InlineData("plusPrefix")]
    [InlineData("paddedName")]
    [InlineData("combinedFlags")]
    [InlineData("wrongCase")]
    public async Task Rejects_summaries_that_are_not_exactly_the_documented_shape(string defect)
    {
        using var f = new Fixture();
        JsonObject entry = f.Entry("alpha-run", ProfileBytes());
        // Reparsed so property names are matched exactly, as in a file.
        JsonObject summary = JsonNode.Parse(entry["summary"]!.ToJsonString())!.AsObject();
        entry["summary"] = summary;
        switch (defect)
        {
            case "badEnum":
                summary["leftTrigger"] = "gecersiz";
                break;
            case "numberEnum":
                summary["gyroMouse"] = 999;
                break;
            case "nullEnum":
                summary["rightTrigger"] = null;
                break;
            case "caseDuplicate":
                summary["Emulation"] = "xbox360";
                break;
            case "plusPrefix":
                summary["leftTrigger"] = "+1";
                break;
            case "paddedName":
                summary["rightTrigger"] = " zones";
                break;
            case "combinedFlags":
                summary["gyroMouse"] = "always, toggle";
                break;
            case "wrongCase":
                summary["gyroStick"] = "WhileHeld";
                break;
            case "stringCount":
                summary["layerCount"] = "1";
                break;
            case "numberBool":
                summary["flickStick"] = 1;
                break;
            default:
                summary["extra"] = 1;
                break;
        }

        f.Publish(Index(1, entry));
        using CatalogService service = f.Service();
        await service.FetchAsync();
        Assert.Equal(CatalogError.BadManifest, service.Status.Error);
    }

    [Fact]
    public async Task Unexpected_failures_end_in_an_error_state_and_do_not_lock_the_catalog()
    {
        using var f = new Fixture();
        f.Publish(Index(1, f.Entry("alpha-run", ProfileBytes())));
        f.Override = _ => throw new NotSupportedException();
        using CatalogService service = f.Service();
        int changes = 0;
        service.Changed += () => changes++;

        await service.FetchAsync();
        Assert.Equal(CatalogState.Error, service.Status.State);
        Assert.Equal(CatalogError.Failed, service.Status.Error);
        Assert.Equal(2, changes);

        f.Override = null;
        await service.FetchAsync();
        Assert.Equal(CatalogState.Ready, service.Status.State);
    }

    [Fact]
    public async Task A_profile_that_cannot_be_saved_keeps_the_list_and_reports_in_the_tab()
    {
        using var f = new Fixture();
        byte[] file = ProfileBytes();
        f.Publish(Index(1, f.Entry("alpha-run", file)), ("profiles/alpha-run.json", file));
        using CatalogService service = f.Service();
        var store = new ProfileStore(f.Dir.Path);
        await service.FetchAsync();
        Directory.Delete(store.ProfilesDirectory, recursive: true);
        File.WriteAllText(store.ProfilesDirectory, "not a folder");
        int changes = 0;
        service.Changed += () => changes++;

        Assert.Null(await service.ImportAsync("alpha-run", store));

        Assert.Equal(CatalogState.Error, service.Status.State);
        Assert.Equal(CatalogError.Storage, service.Status.Error);
        Assert.Single(service.Status.Profiles);
        Assert.Null(service.Status.ImportingId);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Bridge_shows_a_failed_save_in_the_tab_without_an_error_message()
    {
        using var f = new Fixture();
        byte[] file = ProfileBytes();
        f.Publish(Index(1, f.Entry("alpha-run", file)), ("profiles/alpha-run.json", file));
        using var dir = new TempDir();
        using AppCore core = CreateCore(dir);
        using var updates = new UpdateService(Paths(dir), new Version(1, 5, 0));
        var messages = new List<string>();
        using CatalogService service = f.Service();
        using var bridge = new Bridge(core, null!, Collect(messages), Paths(dir), updates, service);
        bridge.Handle("""{"type":"hello"}""", appSource: true);
        bridge.Handle("""{"type":"catalog.list"}""", appSource: true);
        await WaitFor(messages, "catalog.status", s => s.GetProperty("state").GetString() == "ready");
        Directory.Delete(core.Profiles.ProfilesDirectory, recursive: true);
        File.WriteAllText(core.Profiles.ProfilesDirectory, "not a folder");

        bridge.Handle("""{"type":"catalog.import","id":"alpha-run"}""", appSource: true);
        JsonElement failed = await WaitFor(messages, "catalog.status", s => s.GetProperty("state").GetString() == "error");
        Assert.Equal("storage", failed.GetProperty("error").GetString());
        Assert.Equal(1, failed.GetProperty("profiles").GetArrayLength());
        Assert.Empty(Of(messages, "error"));
    }

    [Theory]
    [InlineData("\"5\"")]
    [InlineData("5.5")]
    [InlineData("-1")]
    [InlineData("null")]
    public void Sign_refuses_a_revision_that_is_not_a_whole_number(string revision)
    {
        using var f = new Fixture();
        using var repo = new TempDir();
        WriteRepo(repo, ProfileBytes());
        string index = Path.Combine(repo.Path, "index.json");
        JsonObject root = JsonNode.Parse(File.ReadAllBytes(index))!.AsObject();
        root["revision"] = JsonNode.Parse(revision);
        File.WriteAllText(index, root.ToJsonString());

        Assert.Throws<InvalidDataException>(() => CatalogSigner.Sign(repo.Path, f.Key, DateTime.UtcNow));
    }

    [Fact]
    public void Sign_keeps_counting_from_a_whole_number_revision()
    {
        using var f = new Fixture();
        using var repo = new TempDir();
        WriteRepo(repo, ProfileBytes());
        string index = Path.Combine(repo.Path, "index.json");
        JsonObject root = JsonNode.Parse(File.ReadAllBytes(index))!.AsObject();
        root["revision"] = 5;
        File.WriteAllText(index, root.ToJsonString());

        CatalogSigner.Sign(repo.Path, f.Key, DateTime.UtcNow);
        Assert.Contains("\"revision\": 6", File.ReadAllText(index));
    }

    [Fact]
    public async Task A_byte_order_mark_is_refused_by_signer_and_client()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        using var f = new Fixture();

        using (var repo = new TempDir())
        {
            WriteRepo(repo, [.. bom, .. ProfileBytes()]);
            Assert.Throws<InvalidDataException>(() => CatalogSigner.Sign(repo.Path, f.Key, DateTime.UtcNow));
        }

        using (var repo = new TempDir())
        {
            WriteRepo(repo, ProfileBytes());
            string index = Path.Combine(repo.Path, "index.json");
            File.WriteAllBytes(index, [.. bom, .. File.ReadAllBytes(index)]);
            Assert.Throws<InvalidDataException>(() => CatalogSigner.Sign(repo.Path, f.Key, DateTime.UtcNow));
        }

        byte[] file = [.. bom, .. ProfileBytes()];
        f.Publish(Index(1, f.Entry("alpha-run", file)), ("profiles/alpha-run.json", file));
        using CatalogService service = f.Service();
        await service.FetchAsync();
        Assert.Null(await service.ImportAsync("alpha-run", new ProfileStore(f.Dir.Path)));
        Assert.Equal(CatalogError.BadFile, service.Status.Error);

        byte[] index2 = [.. bom, .. Index(1, f.Entry("alpha-run", ProfileBytes()))];
        CatalogException error = Assert.Throws<CatalogException>(() => CatalogManifest.Parse(index2));
        Assert.Equal(CatalogError.BadManifest, error.Code);
    }

    private static Action<string> Collect(List<string> messages) => m =>
    {
        lock (messages)
        {
            messages.Add(m);
        }
    };

    private static List<JsonElement> Of(List<string> messages, string type)
    {
        lock (messages)
        {
            return messages.Select(m => JsonDocument.Parse(m).RootElement.Clone())
                .Where(e => e.GetProperty("type").GetString() == type).ToList();
        }
    }

    private static async Task<JsonElement> WaitFor(List<string> messages, string type, Func<JsonElement, bool> match)
    {
        for (int i = 0; i < 200; i++)
        {
            foreach (JsonElement e in Of(messages, type))
            {
                if (match(e))
                {
                    return e;
                }
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"No {type} message arrived.");
    }

    private static AppCore CreateCore(TempDir dir) =>
        new(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false),
            new ManualTime(), runTimer: false, new FakePlatform().Platform);

    private static AppPaths Paths(TempDir dir) => new(dir.Path, dir.Path, dir.Path, dir.Path);

    private static HttpResponseMessage Redirect(string target) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(target) } };

    private static byte[] ProfileBytes(bool withRun = false)
    {
        var buttons = new Dictionary<string, Binding>
        {
            ["cross"] = new()
            {
                Press = withRun
                    ? new ActionConfig { Kind = ActionKind.Run, Path = @"C:\Tools\example.exe" }
                    : ActionConfig.ForKeys(Tactile.Input.Key.Space),
            },
        };
        var profile = new Profile
        {
            Id = "shared-id",
            Name = "Alpha",
            Games = ["game.exe"],
            Mapping = new MappingConfig { Enabled = true, Buttons = buttons },
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile, TactileJson.Options));
    }

    private static byte[] Index(long revision, params JsonObject[] entries) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schema"] = 1,
            ["revision"] = revision,
            ["updated"] = "2026-11-22",
            ["profiles"] = new JsonArray(entries.Select(e => (JsonNode?)e).ToArray()),
        }.ToJsonString());

    private static string WriteRepo(TempDir repo, byte[] profile, string path = "profiles/alpha-run.json")
    {
        string target = Path.Combine(repo.Path, "profiles", "alpha-run.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, profile);
        var entry = new JsonObject
        {
            ["id"] = "alpha-run", ["name"] = "Alpha", ["games"] = new JsonArray("game.exe"), ["controller"] = "any",
            ["tags"] = new JsonArray("test"), ["author"] = "tester", ["version"] = "1.0.0", ["path"] = path,
            ["sha256"] = new string('0', 64), ["size"] = 1,
            ["description"] = new JsonObject { ["en"] = "English text", ["tr"] = "Türkçe metin" },
            ["summary"] = JsonSerializer.SerializeToNode(CatalogProfileSummary.FromProfile(new Profile()), TactileJson.Options),
        };
        File.WriteAllBytes(Path.Combine(repo.Path, "index.json"),
            Encoding.UTF8.GetBytes(new JsonObject { ["schema"] = 1, ["profiles"] = new JsonArray(entry) }.ToJsonString()));
        return target;
    }

    private sealed class UnknownLength(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public const string IndexUrl = CatalogKey.IndexUrl;
        public const string SignatureUrl = CatalogKey.SignatureUrl;
        public const string Content = CatalogKey.ContentPrefix;

        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public TempDir Dir { get; } = new();
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<string> Requests { get; } = [];
        public Func<Uri, HttpResponseMessage?>? Override { get; set; }
        public Task Hold { get; set; } = Task.CompletedTask;
        public string RevisionFile => Path.Combine(Dir.Path, "catalog-revision.txt");

        public void Publish(byte[] index, params (string Path, byte[] Bytes)[] profiles)
        {
            Files[IndexUrl] = index;
            Files[SignatureUrl] = Encoding.ASCII.GetBytes(Convert.ToBase64String(
                Key.SignData(index, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
            foreach ((string path, byte[] bytes) in profiles)
            {
                Files[Content + path] = bytes;
            }
        }

        public JsonObject Entry(string id, byte[] file, string? path = null) => new()
        {
            ["id"] = id,
            ["name"] = id + "-profile",
            ["games"] = new JsonArray("game.exe"),
            ["controller"] = "edge",
            ["tags"] = new JsonArray("test"),
            ["author"] = "tester",
            ["version"] = "1.0.0",
            ["path"] = path ?? $"profiles/{id}.json",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(file)),
            ["size"] = file.Length,
            ["description"] = new JsonObject { ["en"] = "English text", ["tr"] = "Türkçe metin" },
            ["summary"] = JsonSerializer.SerializeToNode(CatalogProfileSummary.FromProfile(new Profile()), TactileJson.Options),
        };

        public HttpMessageHandler Handler() => new FakeHttp(this);

        public CatalogClient Client() => new("1.5.0", Handler(), Key.ExportSubjectPublicKeyInfo());

        public CatalogService Service() => new(Client(), RevisionFile);

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("TriggerForge/1.5.0", request.Headers.UserAgent.ToString());
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Uri url = request.RequestUri!;
            lock (Requests)
            {
                Requests.Add(url.AbsoluteUri);
            }

            if (Override?.Invoke(url) is { } overridden)
            {
                return overridden;
            }

            return Files.TryGetValue(url.AbsoluteUri, out byte[]? bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private sealed class FakeHttp(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await fixture.Hold.WaitAsync(cancellationToken);
                return fixture.Respond(request);
            }
        }

        public void Dispose()
        {
            Key.Dispose();
            Dir.Dispose();
        }
    }
}
