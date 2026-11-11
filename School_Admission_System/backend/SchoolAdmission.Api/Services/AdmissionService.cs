using Microsoft.EntityFrameworkCore;
using SchoolAdmission.Api.Contracts;
using SchoolAdmission.Api.Data;
using SchoolAdmission.Api.Domain;

namespace SchoolAdmission.Api.Services;

public sealed class AdmissionService(AdmissionDbContext db, HoldOptions holdOptions) : IAdmissionService
{
    public async Task<CreatedClassResponse> CreateClassAsync(CreateClassRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
            throw new ArgumentException("Class name must contain 1 to 200 characters after trimming.");
        if (request.TotalSeats <= 0)
            throw new ArgumentException("Total seats must be greater than zero.");

        var admissionClass = new AdmissionClass
        {
            Id = $"c_{Guid.NewGuid():N}",
            Name = name,
            TotalSeats = request.TotalSeats
        };
        db.Classes.Add(admissionClass);
        await db.SaveChangesAsync(ct);
        return new CreatedClassResponse(admissionClass.Id, name, admissionClass.TotalSeats);
    }

    public async Task<IReadOnlyList<ClassSeatsResponse>> GetClassesAsync(CancellationToken ct)
        => await ClassesWithSeats(db.Classes.OrderBy(x => x.Name).ThenBy(x => x.Id)).ToListAsync(ct);

    public Task<ClassSeatsResponse?> GetClassAsync(string classId, CancellationToken ct)
        => ClassesWithSeats(db.Classes.Where(x => x.Id == classId)).SingleOrDefaultAsync(ct);

    private static IQueryable<ClassSeatsResponse> ClassesWithSeats(IQueryable<AdmissionClass> classes)
    {
        var now = DateTimeOffset.UtcNow;
        return classes.AsNoTracking()
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.TotalSeats,
                HeldSeats = x.Holds.Count(h => h.Status == HoldStatus.HELD && h.ExpiresAtUtc > now),
                ConfirmedSeats = x.Holds.Count(h => h.Status == HoldStatus.CONFIRMED)
            })
            .Select(x => new ClassSeatsResponse(x.Id, x.Name, x.TotalSeats,
                x.HeldSeats, x.ConfirmedSeats,
                Math.Max(0, x.TotalSeats - x.HeldSeats - x.ConfirmedSeats)));
    }

    public async Task<CreatedHoldResponse> CreateHoldAsync(string classId, string studentId, CancellationToken ct)
    {
        ValidateStudentId(studentId);
        if (string.IsNullOrEmpty(classId) || !classId.StartsWith("c_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(classId[2..], "N", out _))
            throw new ArgumentException("Class ID must be c_ followed by a 32-character hexadecimal GUID.");

        // Every allocation for this class takes the same PostgreSQL row lock.
        // READ COMMITTED gives each subsequent query a fresh view after any lock wait.
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, ct);
        var admissionClass = await LockClassAsync(classId, ct);

        // Take time AFTER obtaining the lock so queued requests do not use stale deadlines.
        var now = DateTimeOffset.UtcNow;
        await db.Holds.Where(h => h.ClassId == classId && h.Status == HoldStatus.HELD && h.ExpiresAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(h => h.Status, HoldStatus.EXPIRED), ct);

        var active = db.Holds.Where(h => h.ClassId == classId &&
            (h.Status == HoldStatus.CONFIRMED || (h.Status == HoldStatus.HELD && h.ExpiresAtUtc > now)));
        if (await active.AnyAsync(h => h.StudentId == studentId, ct))
            throw new AdmissionConflictException("This student already has an active application in this class.");
        if (await active.CountAsync(ct) >= admissionClass.TotalSeats)
            throw new AdmissionConflictException("No seats are available in this class.");

        var hold = new AdmissionHold
        {
            Id = $"h_{Guid.NewGuid():N}", ClassId = classId, StudentId = studentId,
            Status = HoldStatus.HELD, CreatedAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(holdOptions.DurationSeconds)
        };
        db.Holds.Add(hold);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new CreatedHoldResponse(hold.Id, classId, studentId, "HELD", hold.ExpiresAtUtc.UtcDateTime);
    }

    public async Task<HoldDetailsResponse?> GetHoldAsync(string holdId, CancellationToken ct)
    {
        ValidateHoldId(holdId);
        var hold = await db.Holds.AsNoTracking().SingleOrDefaultAsync(h => h.Id == holdId, ct);
        return hold is null ? null : new HoldDetailsResponse(hold.Id, hold.ClassId,
            EffectiveStatus(hold, DateTimeOffset.UtcNow).ToString(), hold.ExpiresAtUtc.UtcDateTime);
    }

    public async Task<ConfirmHoldResponse> ConfirmAsync(string holdId, string studentId, CancellationToken ct)
    {
        await ChangeHoldAsync(holdId, studentId, confirm: true, ct);
        return new ConfirmHoldResponse(holdId, "CONFIRMED");
    }

    public Task CancelAsync(string holdId, string studentId, CancellationToken ct)
        => ChangeHoldAsync(holdId, studentId, confirm: false, ct);

    private async Task ChangeHoldAsync(string holdId, string studentId, bool confirm, CancellationToken ct)
    {
        ValidateStudentId(studentId);
        ValidateHoldId(holdId);
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, ct);
        var classId = await db.Holds.Where(h => h.Id == holdId).Select(h => h.ClassId).SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Hold not found.");
        // All writers, including the worker, lock class first. Re-read after the lock wait.
        await LockClassAsync(classId, ct);
        var hold = await db.Holds.AsNoTracking().SingleAsync(h => h.Id == holdId, ct);
        if (hold.StudentId != studentId)
            throw new KeyNotFoundException("Hold not found.");
        if (confirm && hold.Status == HoldStatus.CONFIRMED)
        {
            await transaction.CommitAsync(ct);
            return; // Same-owner confirmation retries are idempotent.
        }

        var now = DateTimeOffset.UtcNow;
        var held = db.Holds.Where(h => h.Id == holdId && h.Status == HoldStatus.HELD);
        if (hold.Status == HoldStatus.HELD && hold.ExpiresAtUtc <= now)
        {
            await held.ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HoldStatus.EXPIRED), ct);
            await transaction.CommitAsync(ct); // Persist expiry even though this request is rejected.
            throw new AdmissionConflictException("This hold has expired.");
        }
        if (hold.Status != HoldStatus.HELD)
            throw new AdmissionConflictException($"Cannot {(confirm ? "confirm" : "cancel")} a {hold.Status} hold.");

        var eligible = held.Where(h => h.ExpiresAtUtc > now);
        var changed = confirm
            ? await eligible.ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HoldStatus.CONFIRMED)
                .SetProperty(h => h.ConfirmedAtUtc, now), ct)
            : await eligible.ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HoldStatus.CANCELLED)
                .SetProperty(h => h.CancelledAtUtc, now), ct);
        if (changed != 1)
            throw new AdmissionConflictException("The hold is no longer eligible for this action.");
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<StudentHoldResponse>> GetMyHoldsAsync(string studentId, CancellationToken ct)
    {
        ValidateStudentId(studentId);
        var holds = await db.Holds.AsNoTracking().Include(h => h.AdmissionClass)
            .Where(h => h.StudentId == studentId).OrderByDescending(h => h.CreatedAtUtc).ThenBy(h => h.Id)
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return holds.Select(h => new StudentHoldResponse(h.Id, h.ClassId, h.AdmissionClass.Name,
            EffectiveStatus(h, now).ToString(), h.ExpiresAtUtc.UtcDateTime)).ToList();
    }

    public async Task ExpireHoldsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var classIds = await db.Holds.Where(h => h.Status == HoldStatus.HELD && h.ExpiresAtUtc <= now)
            .Select(h => h.ClassId).Distinct().OrderBy(id => id).ToListAsync(ct);
        foreach (var classId in classIds)
        {
            // One class per transaction avoids holding locks across unrelated classes.
            await using var transaction = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, ct);
            await LockClassAsync(classId, ct);
            var deadline = DateTimeOffset.UtcNow;
            await db.Holds.Where(h => h.ClassId == classId && h.Status == HoldStatus.HELD && h.ExpiresAtUtc <= deadline)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HoldStatus.EXPIRED), ct);
            await transaction.CommitAsync(ct);
        }
    }

    private async Task<AdmissionClass> LockClassAsync(string classId, CancellationToken ct)
        => await db.Classes
            .FromSqlInterpolated($"SELECT * FROM \"Classes\" WHERE \"Id\" = {classId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException("Class not found.");

    private static HoldStatus EffectiveStatus(AdmissionHold hold, DateTimeOffset now)
        => hold.Status == HoldStatus.HELD && hold.ExpiresAtUtc <= now ? HoldStatus.EXPIRED : hold.Status;

    private static void ValidateStudentId(string studentId)
    {
        if (string.IsNullOrEmpty(studentId) || studentId.Length != 17 ||
            studentId.Any(character => character < '0' || character > '9'))
            throw new ArgumentException("X-Student-Id must contain exactly 17 ASCII digits.");
    }

    private static void ValidateHoldId(string holdId)
    {
        if (string.IsNullOrEmpty(holdId) || !holdId.StartsWith("h_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(holdId[2..], "N", out _))
            throw new ArgumentException("Hold ID must be h_ followed by a 32-character hexadecimal GUID.");
    }
}
