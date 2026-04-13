using LLMWrapperGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace LLMWrapperGateway.Data;

public class WrapperDbContext : DbContext
{
    public WrapperDbContext(DbContextOptions<WrapperDbContext> options) : base(options) { }

    public DbSet<WrapperConfig> Wrappers => Set<WrapperConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WrapperConfig>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(255).IsRequired();
            entity.Property(e => e.Provider).HasMaxLength(50).IsRequired();
            entity.Property(e => e.BaseUrl).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.Session).HasMaxLength(512);
            entity.Property(e => e.RequestMapping).HasColumnType("text");
            entity.Property(e => e.ResponsePath).HasMaxLength(512);
        });
    }
}
