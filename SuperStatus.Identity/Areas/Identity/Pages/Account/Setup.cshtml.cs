using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities.Identity;

namespace SuperStatus.Identity.Areas.Identity.Pages.Account;

[AllowAnonymous]
public class SetupModel(
    UserManager<SuperStatusIdentityUser> userManager,
    RoleManager<IdentityRole> roleManager,
    SignInManager<SuperStatusIdentityUser> signInManager,
    ILogger<SetupModel> logger) : PageModel
{
    private readonly UserManager<SuperStatusIdentityUser> _userManager = userManager;
    private readonly RoleManager<IdentityRole> _roleManager = roleManager;
    private readonly SignInManager<SuperStatusIdentityUser> _signInManager = signInManager;
    private readonly ILogger<SetupModel> _logger = logger;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    public class InputModel
    {
        [Required]
        [EmailAddress]
        [Display(Name = "Email")]
        public string Email { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Display(Name = "Confirm password")]
        [Compare(nameof(Password), ErrorMessage = "The password and confirmation password do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        if (await AnyUserExistsAsync())
        {
            return NotFound();
        }

        ReturnUrl = returnUrl;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;

        // Re-check under the same request: if a user has appeared between
        // GET and POST (race / direct POST after another operator finished
        // setup), treat the form as gone.
        if (await AnyUserExistsAsync())
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (!await _roleManager.RoleExistsAsync(SuperStatusIdentityDbInitializer.AdministratorRole))
        {
            var roleResult = await _roleManager.CreateAsync(
                new IdentityRole(SuperStatusIdentityDbInitializer.AdministratorRole));
            if (!roleResult.Succeeded)
            {
                foreach (var error in roleResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }
                return Page();
            }
        }

        var user = new SuperStatusIdentityUser
        {
            UserName = Input.Email,
            Email = Input.Email,
            EmailConfirmed = true
        };

        var createResult = await _userManager.CreateAsync(user, Input.Password);
        if (!createResult.Succeeded)
        {
            foreach (var error in createResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }
            return Page();
        }

        var roleAssign = await _userManager.AddToRoleAsync(
            user, SuperStatusIdentityDbInitializer.AdministratorRole);
        if (!roleAssign.Succeeded)
        {
            foreach (var error in roleAssign.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }
            return Page();
        }

        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogInformation("First-run setup completed: created administrator {Email}.", Input.Email);

        return LocalRedirect(returnUrl ?? "~/");
    }

    private Task<bool> AnyUserExistsAsync() =>
        _userManager.Users.AsNoTracking().AnyAsync();
}
