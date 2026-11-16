using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using QuasarBridge.Options;

namespace QuasarBridge.Controllers;

[ApiController]
[Route("api/config")]
public class ConfigController(IOptions<QuasarBridgeOptions> options) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var o = options.Value;
        // Do not leak cert password.
        return Ok(new
        {
            o.Mode,
            o.ListenUrl,
            o.UpstreamUrl,
            o.HttpPort,
            o.TlsPort,
            o.TlsBindAddress,
            o.EnableTls,
            o.TlsCertPath,
            o.ResolveUpstreamViaDoh,
            o.SwaggerEnabled,
            o.DatabasePath,
            o.AllowedClientIps
        });
    }
}
