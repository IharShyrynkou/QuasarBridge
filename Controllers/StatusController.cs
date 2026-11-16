using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using QuasarBridge.Options;
using QuasarBridge.Services;

namespace QuasarBridge.Controllers;

[ApiController]
[Route("api/status")]
public class StatusController(
    ProxyState state,
    IOptions<QuasarBridgeOptions> options,
    MessageRouter router) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var o = options.Value;
        return Ok(new
        {
            mode = o.Mode,
            database = o.DatabasePath,
            tls = new { enabled = o.EnableTls, bind = o.TlsBindAddress, port = o.TlsPort, cert = o.TlsCertPath },
            routing = new { revision = router.Revision, rules = router.RuleCount },
            proxy = state.Snapshot($"http://127.0.0.1:{o.HttpPort}", o.UpstreamUrl, "/uni.ws")
        });
    }
}
