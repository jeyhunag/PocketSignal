using Microsoft.EntityFrameworkCore;
using PocketSignal.Api.Data.Entities;

namespace PocketSignal.Api.Data;

public class PocketSignalDbContext : DbContext
{
    public PocketSignalDbContext(DbContextOptions<PocketSignalDbContext> options)
        : base(options)
    {
    }

    public DbSet<ForexSignalEntity> ForexSignals => Set<ForexSignalEntity>();

    public DbSet<ForexStrategyScoreEntity> ForexStrategyScores => Set<ForexStrategyScoreEntity>();

    public DbSet<ForexTradeResultEntity> ForexTradeResults => Set<ForexTradeResultEntity>();

    public DbSet<BinaryTradeResultEntity> BinaryTradeResults => Set<BinaryTradeResultEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<BinaryTradeResultEntity>(entity =>
        {
            entity.ToTable("BinaryTradeResults");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Symbol).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Direction).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Grade).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Result).HasMaxLength(30).IsRequired();

            entity.Property(x => x.EntryPrice).HasPrecision(18, 5);
            entity.Property(x => x.ExitPrice).HasPrecision(18, 5);
            entity.Property(x => x.Difference).HasPrecision(18, 5);

            entity.HasIndex(x => x.CreatedAtUtc);
            entity.HasIndex(x => x.DueAtUtc);
            entity.HasIndex(x => x.Symbol);
            entity.HasIndex(x => x.Result);
        });

        modelBuilder.Entity<ForexSignalEntity>(entity =>
        {
            entity.ToTable("ForexSignals");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Symbol).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Direction).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Grade).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(30).IsRequired();

            entity.Property(x => x.EntryPrice).HasPrecision(18, 5);
            entity.Property(x => x.StopLoss).HasPrecision(18, 5);
            entity.Property(x => x.TakeProfit1).HasPrecision(18, 5);
            entity.Property(x => x.TakeProfit2).HasPrecision(18, 5);

            entity.Property(x => x.RiskPips).HasPrecision(18, 2);
            entity.Property(x => x.RewardPips1).HasPrecision(18, 2);
            entity.Property(x => x.RewardPips2).HasPrecision(18, 2);
            entity.Property(x => x.RiskReward1).HasPrecision(18, 2);
            entity.Property(x => x.RiskReward2).HasPrecision(18, 2);

            entity.HasMany(x => x.StrategyScores)
                .WithOne(x => x.ForexSignal)
                .HasForeignKey(x => x.ForexSignalId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.TradeResult)
                .WithOne(x => x.ForexSignal)
                .HasForeignKey<ForexTradeResultEntity>(x => x.ForexSignalId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ForexStrategyScoreEntity>(entity =>
        {
            entity.ToTable("ForexStrategyScores");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.StrategyName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Direction).HasMaxLength(20).IsRequired();
        });

        modelBuilder.Entity<ForexTradeResultEntity>(entity =>
        {
            entity.ToTable("ForexTradeResults");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Symbol).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Direction).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Result).HasMaxLength(30).IsRequired();

            entity.Property(x => x.EntryPrice).HasPrecision(18, 5);
            entity.Property(x => x.StopLoss).HasPrecision(18, 5);
            entity.Property(x => x.TakeProfit1).HasPrecision(18, 5);
            entity.Property(x => x.TakeProfit2).HasPrecision(18, 5);
            entity.Property(x => x.ExitPrice).HasPrecision(18, 5);
            entity.Property(x => x.Difference).HasPrecision(18, 5);

            entity.HasIndex(x => x.CreatedAtUtc);
            entity.HasIndex(x => x.Symbol);
            entity.HasIndex(x => x.Result);
        });
    }
}