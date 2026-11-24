using Microsoft.AspNetCore.Mvc;

namespace Sattleme.E2e;

/// <summary>What the end-to-end test asks of its fake: the invoices made, and to pay one.</summary>
public class E2eController : Controller
{
    private readonly E2eGateway _gateway;

    /// <summary>The controller.</summary>
    public E2eController(E2eGateway gateway)
    {
        _gateway = gateway;
    }

    /// <summary>The references of the invoices made.</summary>
    [HttpGet("e2e-sattleme/references")]
    public IActionResult References()
    {
        return Json(_gateway.References);
    }

    /// <summary>Pays the invoice of <paramref name="reference"/>.</summary>
    [HttpPost("e2e-sattleme/pay/{reference}")]
    [IgnoreAntiforgeryToken]
    public IActionResult Pay(string reference)
    {
        return _gateway.Pay(reference) ? Ok() : NotFound();
    }
}
