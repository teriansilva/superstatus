using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SuperStatus.Data.ViewModels;

namespace SuperStatus.Identity.Controllers;

public class ErrorController : Controller
{
    // #154: the single canonical /error surface (the colliding Razor Pages
    // /Error page was removed — both matched "/error" case-insensitively and
    // threw AmbiguousMatchException). AllowAnonymous so the error page always
    // renders; an error handler that required auth could redirect-loop.
    [AllowAnonymous]
    [Route("error")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        // If the error was not caused by an invalid
        // OIDC request, display a generic error page.
        var response = HttpContext.GetOpenIddictServerResponse();
        if (response == null)
        {
            return View(new ErrorViewModel());
        }

        return View(new ErrorViewModel
        {
            Error = response!.Error ?? string.Empty,
            ErrorDescription = response!.ErrorDescription ?? string.Empty
        });
    }
}