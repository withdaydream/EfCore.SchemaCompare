using Microsoft.EntityFrameworkCore;

namespace DataLayer.SchemaDb;

public class CheckConstraintsContext : DbContext
{
    public CheckConstraintsContext(DbContextOptions<CheckConstraintsContext> options)
        : base(options) { }

    public DbSet<Book> Books { get; set; }
    public DbSet<Author> Authors { get; set; }
    public DbSet<Review> Reviews { get; set; }

    protected override void
        OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Book>().ToTable("Book");
        modelBuilder.Entity<Author>().ToTable("Author");
        modelBuilder.Entity<Review>().ToTable("Review");
    }
}