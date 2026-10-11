using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Nginx;

public sealed class NginxAgentService(NginxManager nginx) : NginxAgent.NginxAgentBase
{
    public override Task<NginxStatus> GetNginxStatus(Empty request, ServerCallContext context) =>
        nginx.GetStatusAsync(context.CancellationToken);

    public override Task<NginxApplyResult> ApplyNginxConfig(NginxConfig request, ServerCallContext context) =>
        nginx.ApplyAsync(request, context.CancellationToken);

    public override Task<NginxApplyResult> RemoveNginxConfig(NginxConfigRef request, ServerCallContext context) =>
        nginx.RemoveAsync(request, context.CancellationToken);

    public override Task<NginxConfigs> ListNginxConfigs(Empty request, ServerCallContext context) =>
        Task.FromResult(nginx.List());

    public override Task<CertificateInfo> InstallCertificate(InstallCertificateRequest request, ServerCallContext context) =>
        nginx.InstallCertificateAsync(request, context.CancellationToken);

    public override Task<Empty> RemoveCertificate(CertificateRef request, ServerCallContext context)
    {
        nginx.RemoveCertificate(request.Name);

        return Task.FromResult(new Empty());
    }

    public override Task<CertificateList> ListCertificates(Empty request, ServerCallContext context) =>
        Task.FromResult(nginx.ListCertificates());

    public override async Task<Empty> WriteAcmeChallenge(AcmeChallenge request, ServerCallContext context)
    {
        await nginx.WriteAcmeChallengeAsync(request, context.CancellationToken);

        return new Empty();
    }

    public override Task<Empty> RemoveAcmeChallenge(AcmeChallenge request, ServerCallContext context)
    {
        nginx.RemoveAcmeChallenge(request);

        return Task.FromResult(new Empty());
    }

    public override Task<NginxApplyResult> CloseSetupAddress(Empty request, ServerCallContext context) =>
        nginx.CloseSetupAddressAsync(context.CancellationToken);

    public override Task<WebRootResult> EnsureWebRoot(WebRootRequest request, ServerCallContext context) =>
        Task.FromResult(nginx.EnsureWebRoot(request));
}
