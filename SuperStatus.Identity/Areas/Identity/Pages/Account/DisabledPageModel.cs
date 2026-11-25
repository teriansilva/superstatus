using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SuperStatus.Identity.Areas.Identity.Pages.Account;

// Base for Identity default-UI pages we explicitly disable. Scaffolding the
// page into the project wins routing priority over the framework default
// shipped in Microsoft.AspNetCore.Identity.UI; returning NotFound from both
// handlers takes the page out of service without removing AddDefaultUI()
// (which is still needed for the Manage/* + 2fa + AccessDenied / Lockout
// pages we DO keep).
[AllowAnonymous]
public abstract class DisabledPageModel : PageModel
{
    public IActionResult OnGet() => NotFound();
    public IActionResult OnPost() => NotFound();
}
