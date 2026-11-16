using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using QuasarBridge.Options;
using QuasarBridge.Services;

namespace QuasarBridge.Controllers;

[ApiController]
[Route("healthz")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get(IOptions<QuasarBridgeOptions> options, MessageRouter router, ProxyState state)
    {
        var o = options.Value;
        return Ok(new
        {
            status = "ok",
            mode = o.Mode,
            upstream = o.UpstreamUrl,
            doh = o.ResolveUpstreamViaDoh,
            routingRevision = router.Revision,
            routingRules = router.RuleCount,
            activeConnections = state.Snapshot(o.ListenUrl, o.UpstreamUrl, "/uni.ws")
        });
    }
}
