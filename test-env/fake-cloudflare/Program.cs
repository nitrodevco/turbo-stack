using TurboManager.FakeCloudflare;

// Docker test environment: listens on http://0.0.0.0:8080 (base URL http://fake-cloudflare:8080/client/v4).
var app = FakeCloudflareServer.Create(args, Environment.GetEnvironmentVariable("FAKE_CLOUDFLARE_URLS") ?? "http://0.0.0.0:8080");
app.Run();
