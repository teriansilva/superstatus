using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.Entities.Identity;

namespace SuperStatus.Identity.Areas.Identity.Pages.Account;

[AllowAnonymous]
public class LoginModel(
    SignInManager<SuperStatusIdentityUser> signInManager,
    UserManager<SuperStatusIdentityUser> userManager,
    ILogger<LoginModel> logger) : PageModel
{
    private readonly SignInManager<SuperStatusIdentityUser> _signInManager = signInManager;
    private readonly UserManager<SuperStatusIdentityUser> _userManager = userManager;
    private readonly ILogger<LoginModel> _logger = logger;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

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

        var result = await _signInManager.PasswordSignInAsync(
            Input.Email,
            Input.Password,
            Input.RememberMe,
            lockoutOnFailure: true);

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
