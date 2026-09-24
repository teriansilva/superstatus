using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities.Identity;
using SuperStatus.Identity.Services;

namespace SuperStatus.Identity.Areas.Identity.Pages.Account;

[AllowAnonymous]
public class LoginModel(
    SignInManager<SuperStatusIdentityUser> signInManager,
    UserManager<SuperStatusIdentityUser> userManager,
    DemoModeInfo demoMode,
    ILogger<LoginModel> logger) : PageModel
{
    private readonly SignInManager<SuperStatusIdentityUser> _signInManager = signInManager;
    private readonly UserManager<SuperStatusIdentityUser> _userManager = userManager;
    private readonly DemoModeInfo _demoMode = demoMode;
    private readonly ILogger<LoginModel> _logger = logger;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Issue #377 — true only on the hosted public demo instance. Gates the
    /// credentials panel and the field prefill in Login.cshtml. Everything the view
    /// renders about the demo credentials hangs off this one property.
    /// </summary>
    public bool IsDemo => _demoMode.IsEnabled;

    /// <summary>The demo credentials, surfaced to the view. Public knowledge by design.</summary>
    public static string DemoEmail => SuperStatusIdentityDbInitializer.DemoAdminEmail;

    public static string DemoPassword => SuperStatusIdentityDbInitializer.DemoAdminPassword;

    public class InputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Remember me?")]
        public bool RememberMe { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        // First-run: if no admin exists yet, send the operator through Setup.
        // The Setup page will sign them in and continue the OIDC flow back
        // to the original returnUrl.
        if (!await _userManager.Users.AsNoTracking().AnyAsync())
        {
            return RedirectToPage("Setup", new { returnUrl });
        }

        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            ModelState.AddModelError(string.Empty, ErrorMessage);
        }

        returnUrl ??= Url.Content("~/");

        // Clear any stale external-provider cookie so a fresh login starts clean.
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        // #377: on the public demo, hand the visitor a working form rather than an
        // empty one they have to copy the credentials into.
        if (IsDemo)
        {
            Input.Email = DemoEmail;
            Input.Password = DemoPassword;
        }

        ReturnUrl = returnUrl;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        // Defensive: a direct POST to /Login on a fresh DB has nothing to
        // sign in against. Route the operator to Setup instead of running
        // PasswordSignInAsync against an empty user table.
        if (!await _userManager.Users.AsNoTracking().AnyAsync())
        {
            return RedirectToPage("Setup", new { returnUrl });
        }

        returnUrl ??= Url.Content("~/");

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // #377: the demo's credentials are published, so anyone could lock the one
        // account out with a handful of deliberate bad passwords and leave the demo
        // unusable until the next hourly reset. Lockout protects a secret; there is no
        // secret here, so on the demo it only provides a griefing vector. It stays ON
        // for every real deployment.
        var result = await _signInManager.PasswordSignInAsync(
            Input.Email,
            Input.Password,
            Input.RememberMe,
            lockoutOnFailure: !IsDemo);

        if (result.Succeeded)
        {
            _logger.LogInformation("User {Email} logged in.", Input.Email);
            return LocalRedirect(returnUrl);
        }
        if (result.RequiresTwoFactor)
        {
            return RedirectToPage("./LoginWith2fa", new { ReturnUrl = returnUrl, Input.RememberMe });
        }
        if (result.IsLockedOut)
        {
            _logger.LogWarning("User {Email} locked out.", Input.Email);
            return RedirectToPage("./Lockout");
        }

        ModelState.AddModelError(string.Empty, "Invalid login attempt.");
        return Page();
    }
}
