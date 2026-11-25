using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.Entities.Identity;

namespace SuperStatus.Data.DatabaseContext;

public class SuperStatusIdentityDb : IdentityDbContext<SuperStatusIdentityUser>
{
    public SuperStatusIdentityDb(DbContextOptions<SuperStatusIdentityDb> options)
        : base(options)
    {
    }
}
