using Amicus.Domain;
using Amicus.Domain.Entities;
using Amicus.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Amicus.Infrastructure;

public class AmicusDbContext(DbContextOptions<AmicusDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<Event> Events => Set<Event>();

    public DbSet<Specialist> Specialists => Set<Specialist>();

    public DbSet<EventSpecialist> EventSpecialists => Set<EventSpecialist>();

    public DbSet<SlotPattern> SlotPatterns => Set<SlotPattern>();

    public DbSet<Slot> Slots => Set<Slot>();

    public DbSet<Booking> Bookings => Set<Booking>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Identity calls ToTable("AspNetUsers") itself, so UseSnakeCaseNamingConvention
        // leaves its tables PascalCase while ours are snake_case. Renaming them keeps
        // one convention across the whole schema — worth doing now, while no data or
        // query anywhere depends on the old names.
        builder.Entity<AppUser>().ToTable("users");
        builder.Entity<AppRole>().ToTable("roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");

        builder.Entity<Event>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Slug).HasMaxLength(100);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.HasIndex(x => x.Slug).IsUnique();
        });

        builder.Entity<Specialist>(e =>
        {
            e.Property(x => x.FullName).HasMaxLength(200);
            e.Property(x => x.Specialty).HasMaxLength(100);
            e.Property(x => x.Bio).HasMaxLength(2000);

            // Stored as text, not an int: a human reading the table sees
            // 'Medical', and a new category is a code change, not a magic number.
            e.Property(x => x.Category)
                .HasConversion<string>()
                .HasMaxLength(20)
                .HasDefaultValue(SpecialistCategory.Social);

            // One identity user is at most one specialist, but most specialists
            // have no account at all — so the uniqueness has to skip the nulls.
            e.HasIndex(x => x.UserId)
                .IsUnique()
                .HasFilter("user_id IS NOT NULL");
        });

        builder.Entity<EventSpecialist>(e =>
        {
            e.Property(x => x.Location).HasMaxLength(200);

            e.HasOne(x => x.Event)
                .WithMany(x => x.Specialists)
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Specialist)
                .WithMany(x => x.Events)
                .HasForeignKey(x => x.SpecialistId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.EventId, x.SpecialistId }).IsUnique();
        });

        builder.Entity<SlotPattern>(e =>
        {
            e.HasOne(x => x.EventSpecialist)
                .WithMany(x => x.Patterns)
                .HasForeignKey(x => x.EventSpecialistId)
                .OnDelete(DeleteBehavior.Cascade);

            // Guard the invariants in the database too, not just in SlotPlanner —
            // a bad row inserted by hand would otherwise generate nonsense slots.
            e.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "ck_slot_pattern_duration_positive", "slot_duration_minutes > 0");
                t.HasCheckConstraint(
                    "ck_slot_pattern_break_non_negative", "break_minutes >= 0");
                t.HasCheckConstraint(
                    "ck_slot_pattern_window_ordered", "end_time > start_time");
            });
        });

        builder.Entity<Slot>(e =>
        {
            e.HasOne(x => x.EventSpecialist)
                .WithMany(x => x.Slots)
                .HasForeignKey(x => x.EventSpecialistId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.SlotPattern)
                .WithMany()
                .HasForeignKey(x => x.SlotPatternId)
                .OnDelete(DeleteBehavior.SetNull);

            // Re-running slot generation must not duplicate the board.
            e.HasIndex(x => new { x.EventSpecialistId, x.StartsAt }).IsUnique();

            e.ToTable(t => t.HasCheckConstraint(
                "ck_slot_ends_after_start", "ends_at > starts_at"));
        });

        builder.Entity<Booking>(e =>
        {
            e.Property(x => x.Topic).HasMaxLength(500);
            e.Property(x => x.CheckInCode).HasMaxLength(32);

            // Stored as text, not an int: a human reading the table sees
            // 'Cancelled', and the partial index below stays self-explanatory.
            e.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(20);

            e.HasOne(x => x.Slot)
                .WithMany(x => x.Bookings)
                .HasForeignKey(x => x.SlotId)
                .OnDelete(DeleteBehavior.Cascade);

            // THE double-booking guard. One live booking per slot, enforced by
            // Postgres, so two students tapping the same slot in the same second
            // cannot both win — the loser gets a unique violation to handle.
            // Cancelled bookings are excluded so a freed slot can be re-taken
            // while its history stays on the row.
            e.HasIndex(x => x.SlotId)
                .IsUnique()
                .HasDatabaseName("ux_booking_live_slot")
                .HasFilter($"status <> '{nameof(BookingStatus.Cancelled)}'");

            e.HasIndex(x => x.CheckInCode).IsUnique();
            e.HasIndex(x => x.StudentUserId);
        });
    }
}
