using Microsoft.AspNetCore.Mvc;
using QuasarBridge.Services;

namespace QuasarBridge.Controllers;

[ApiController]
[Route("uni.ws")]
public class UniWsController(UniProxyService proxy) : ControllerBase
{
    [HttpGet]
    public Task Get() => proxy.HandleAsync(HttpContext);
}
