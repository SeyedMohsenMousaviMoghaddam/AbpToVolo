using Abp.Zero.EntityFrameworkCore;
using Acme.PhoneBook.Authorization.Roles;
using Acme.PhoneBook.Authorization.Users;
using Acme.PhoneBook.MultiTenancy;
using Acme.PhoneBook.People;
using Microsoft.EntityFrameworkCore;

namespace Acme.PhoneBook.EntityFrameworkCore
{
    public class PhoneBookDbContext : AbpZeroDbContext<Tenant, Role, User, PhoneBookDbContext>
    {
        public DbSet<Person> People { get; set; }

        public DbSet<Phone> Phones { get; set; }

        public PhoneBookDbContext(DbContextOptions<PhoneBookDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Person>().ToTable("PbPersons");
            modelBuilder.Entity<Phone>().ToTable("PbPhones");
        }
    }
}
