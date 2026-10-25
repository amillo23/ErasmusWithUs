using ErasmusWithUs.Api.Data;
using ErasmusWithUs.Api.Models;
using ErasmusWithUs.Api.Services;
using System.Data.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var jwtKey = builder.Configuration["Jwt:Key"];
if (builder.Environment.IsProduction() &&
    (string.IsNullOrWhiteSpace(jwtKey) || Encoding.UTF8.GetByteCount(jwtKey) < 32))
{
    throw new InvalidOperationException("Set Jwt:Key to a secret of at least 32 bytes before starting the API in production.");
}

jwtKey ??= "ErasmusWithUs-development-key-change-before-production-2026";

builder.Services.AddOpenApi();
var databaseConnectionString = builder.Configuration["Database:ConnectionString"] ??
    builder.Configuration["DATABASE_URL"];
var configuredDatabasePath = builder.Configuration["Database:Path"];
var databasePath = string.IsNullOrWhiteSpace(configuredDatabasePath)
    ? builder.Environment.IsProduction() ? "/var/data/erasmuswithus.db" : "erasmuswithus.db"
    : configuredDatabasePath;
if (string.IsNullOrWhiteSpace(databaseConnectionString) &&
    builder.Environment.IsProduction() && !Path.IsPathRooted(databasePath))
{
    throw new InvalidOperationException("Configure Database:ConnectionString for production PostgreSQL or use an absolute Database:Path with persistent storage.");
}
builder.Services.AddDbContext<ErasmusDbContext>(options =>
{
    if (!string.IsNullOrWhiteSpace(databaseConnectionString))
        options.UseNpgsql(databaseConnectionString);
    else
        options.UseSqlite($"Data Source={databasePath}");
});
builder.Services.AddSingleton<IPasswordHasher<UserEntity>, PasswordHasher<UserEntity>>();
builder.Services.AddHostedService<DeadlineReminderWorker>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1)
    });
builder.Services.AddAuthorization();
var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").GetChildren()
    .Select(item => item.Value)
    .Where(item => !string.IsNullOrWhiteSpace(item))
    .Cast<string>()
    .ToArray();
if (configuredOrigins.Length == 0 && builder.Configuration["Cors:AllowedOrigins"] is { } originsValue)
{
    configuredOrigins = originsValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (configuredOrigins.Length > 0)
        policy.WithOrigins(configuredOrigins).AllowAnyHeader().AllowAnyMethod();
}));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "Too many requests. Wait a bit and try again." },
            cancellationToken: cancellationToken);
    };

    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        GetClientIp(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 12,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("account-email", context => RateLimitPartition.GetFixedWindowLimiter(
        GetClientIp(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 4,
            Window = TimeSpan.FromMinutes(15),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("verify-code", context => RateLimitPartition.GetFixedWindowLimiter(
        GetClientIp(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(15),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("community-message", context => RateLimitPartition.GetFixedWindowLimiter(
        GetClientIp(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();
app.Logger.LogInformation(
    string.IsNullOrWhiteSpace(databaseConnectionString)
        ? "SQLite database path: {DatabasePath}"
        : "Using PostgreSQL database",
    string.IsNullOrWhiteSpace(databaseConnectionString) ? Path.GetFullPath(databasePath) : string.Empty);

using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<ErasmusDbContext>();
    database.Database.EnsureCreated();
    EnsureAuthColumns(database);
    EnsureCommunityTables(database);
    EnsureFeatureTables(database);
    EnsurePushTables(database);

    var existingOpportunityIds = database.Opportunities.Select(item => item.Id).ToHashSet();
    var missingOpportunities = SeedData.AllOpportunities
        .Where(opportunity => !existingOpportunityIds.Contains(opportunity.Id))
        .Select(ToEntity)
        .ToArray();
    if (missingOpportunities.Length > 0)
    {
        database.Opportunities.AddRange(missingOpportunities);
        database.SaveChanges();
    }
}

using var dataScope = app.Services.CreateScope();
var dataContext = dataScope.ServiceProvider.GetRequiredService<ErasmusDbContext>();
var opportunities = dataContext.Opportunities
    .AsNoTracking()
    .ToArray()
    .Select(ToOpportunity)
    .ToArray();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/api/auth/register", async (RegisterRequest request, ErasmusDbContext database,
    IPasswordHasher<UserEntity> passwordHasher) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || !MailAddress.TryCreate(request.Email, out _) ||
        string.IsNullOrWhiteSpace(request.DisplayName))
        return Results.BadRequest(new { message = "Enter a valid email address and name." });

    if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
        return Results.BadRequest(new { message = "Password must be at least 8 characters long." });

    var email = request.Email.Trim().ToLowerInvariant();

    var user = await database.Users.SingleOrDefaultAsync(item => item.Email == email);
    if (user is not null && user.EmailVerified)
        return Results.Conflict(new { message = "An account with this email already exists. Sign in instead." });

    user ??= new UserEntity
    {
        Email = email,
        DisplayName = request.DisplayName.Trim(),
        PasswordHash = string.Empty
    };
    user.DisplayName = request.DisplayName.Trim();
    user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
    user.EmailVerificationToken = GenerateVerificationCode();
    user.EmailVerificationTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
    if (user.Id == 0)
        database.Users.Add(user);
    await database.SaveChangesAsync();
    if (!await SendVerificationCodeAsync(user.Email, user.EmailVerificationToken, builder.Configuration, app.Logger, app.Environment.IsDevelopment()))
        return Results.Problem("The account was created, but the verification email could not be sent. Check the SMTP settings in the deployment environment.", statusCode: StatusCodes.Status503ServiceUnavailable);

    return Results.Ok(CreateAuthResponse(user, jwtKey, true,
        app.Environment.IsDevelopment() ? user.EmailVerificationToken : null));
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/login", async (LoginRequest request, ErasmusDbContext database,
    IPasswordHasher<UserEntity> passwordHasher) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        return Results.Json(new { message = "Invalid email or password. Create an ErasmusWithUs account first." }, statusCode: StatusCodes.Status401Unauthorized);

    var email = request.Email.Trim().ToLowerInvariant();
    var user = await database.Users.SingleOrDefaultAsync(item => item.Email == email);
    if (user is null || passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password)
        == PasswordVerificationResult.Failed)
        return Results.Json(new { message = "Invalid email or password. Create an ErasmusWithUs account first." }, statusCode: StatusCodes.Status401Unauthorized);

    if (!user.EmailVerified)
    {
        user.EmailVerificationToken = GenerateVerificationCode();
        user.EmailVerificationTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        await database.SaveChangesAsync();
        if (!await SendVerificationCodeAsync(user.Email, user.EmailVerificationToken, builder.Configuration, app.Logger, app.Environment.IsDevelopment()))
            return Results.Problem("The verification email could not be sent. Check the SMTP settings in the deployment environment.", statusCode: StatusCodes.Status503ServiceUnavailable);
        return Results.Ok(CreateAuthResponse(user, jwtKey, true,
            app.Environment.IsDevelopment() ? user.EmailVerificationToken : null));
    }

    return Results.Ok(CreateAuthResponse(user, jwtKey));
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/forgot-password", async (ForgotPasswordRequest request, ErasmusDbContext database) =>
{
    var email = request.Email?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(email) || !MailAddress.TryCreate(email, out _))
        return Results.BadRequest(new { message = "Enter a valid email address." });

    var user = await database.Users.SingleOrDefaultAsync(item => item.Email == email);
    string? resetToken = null;

    if (user is not null)
    {
        resetToken = SeedData.GenerateToken();
        user.PasswordResetToken = HashToken(resetToken);
        user.PasswordResetTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        await database.SaveChangesAsync();

        var sent = await SendEmailAsync(
            user.Email,
            "Reset your Erasmus With Us password",
            $"Your password reset code is:\n\n{resetToken}\n\nIt expires in one hour. If you did not request a password reset, you can ignore this email.",
            builder.Configuration,
            app.Logger,
            app.Environment.IsDevelopment());

        if (!sent)
            app.Logger.LogWarning("Password reset email could not be sent for account {UserId}.", user.Id);
    }

    return Results.Ok(new
    {
        message = "If an account exists for that email, a password reset code has been sent.",
        token = app.Environment.IsDevelopment() ? resetToken : null
    });
}).RequireRateLimiting("account-email");

app.MapPost("/api/auth/reset-password", async (ResetPasswordRequest request, ErasmusDbContext database,
    IPasswordHasher<UserEntity> passwordHasher) =>
{
    var email = request.Email?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(email))
        return Results.BadRequest(new { message = "Enter a valid email address." });

    var user = await database.Users.SingleOrDefaultAsync(item => item.Email == email);
    if (user is null)
        return Results.BadRequest(new { message = "This password reset code is invalid or expired." });

    if (string.IsNullOrWhiteSpace(request.Token) ||
        !MatchesToken(request.Token, user.PasswordResetToken) ||
        user.PasswordResetTokenExpiresAt is null ||
        user.PasswordResetTokenExpiresAt.Value < DateTimeOffset.UtcNow)
        return Results.BadRequest(new { message = "This password reset token is invalid or expired." });

    if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
        return Results.BadRequest(new { message = "Password must be at least 8 characters long." });

    user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
    user.PasswordResetToken = null;
    user.PasswordResetTokenExpiresAt = null;
    await database.SaveChangesAsync();

    return Results.Ok(new { message = "Password updated successfully." });
}).RequireRateLimiting("account-email");

app.MapPost("/api/auth/request-email-verification", async (EmailVerificationRequest request, ErasmusDbContext database) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || !MailAddress.TryCreate(request.Email, out _))
        return Results.BadRequest(new { message = "Enter a valid email address." });

    var email = request.Email.Trim().ToLowerInvariant();
    var user = await database.Users.SingleOrDefaultAsync(item => item.Email == email);

    if (user is not null)
    {
        user.EmailVerificationToken = GenerateVerificationCode();
        user.EmailVerificationTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        await database.SaveChangesAsync();
        if (!await SendVerificationCodeAsync(user.Email, user.EmailVerificationToken, builder.Configuration, app.Logger, app.Environment.IsDevelopment()))
            return Results.Problem("The verification email could not be sent. Check the SMTP settings in the deployment environment.", statusCode: StatusCodes.Status503ServiceUnavailable);
        return Results.Ok(new { message = "The verification code was sent.", token = app.Environment.IsDevelopment() ? user.EmailVerificationToken : null });
    }

    return Results.Ok(new { message = "If the email exists, a verification link is available." });
}).RequireRateLimiting("account-email");

app.MapPost("/api/auth/verify-email", async (VerifyEmailRequest request, ErasmusDbContext database) =>
{
    var email = request.Email.Trim().ToLowerInvariant();
    var user = await database.Users.SingleOrDefaultAsync(item => item.Email == email);
    if (user is null)
        return Results.BadRequest(new { message = "No account matches this email address." });

    if (string.IsNullOrWhiteSpace(request.Token) ||
        user.EmailVerificationToken != request.Token ||
        user.EmailVerificationTokenExpiresAt is null ||
        user.EmailVerificationTokenExpiresAt.Value < DateTimeOffset.UtcNow)
        return Results.BadRequest(new { message = "This email verification token is invalid or expired." });

    user.EmailVerified = true;
    user.EmailVerificationToken = null;
    user.EmailVerificationTokenExpiresAt = null;
    await database.SaveChangesAsync();

    return Results.Ok(new { message = "Email verified successfully." });
}).RequireRateLimiting("verify-code");

app.MapGet("/api/auth/me", async (ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    var user = userId is null ? null : await database.Users.FindAsync(userId.Value);
    return user is null || !user.EmailVerified ? Results.Unauthorized() : Results.Ok(ToUserResponse(user));
}).RequireAuthorization();

app.MapPut("/api/profile", async (UpdateProfileRequest request, ClaimsPrincipal principal,
    ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    var user = userId is null ? null : await database.Users.FindAsync(userId.Value);
    if (user is null || !user.EmailVerified)
        return Results.Unauthorized();

    if (request.DisplayName is { } displayName && (displayName.Trim().Length < 2 || displayName.Trim().Length > 80))
        return Results.BadRequest(new { message = "Your name must be between 2 and 80 characters." });
    if (request.HomeUniversity?.Length > 160 || request.Intake?.Length > 40 || request.Bio?.Length > 320)
        return Results.BadRequest(new { message = "Check the length of your university, intake, or bio field." });
    if (request.Gpa is < 0 or > 4)
        return Results.BadRequest(new { message = "GPA must be between 0 and 4." });
    if (request.ProfilePhotoDataUrl is not null && !TryNormalizeProfilePhoto(request.ProfilePhotoDataUrl, out _))
        return Results.BadRequest(new { message = "Choose a valid JPEG profile photo smaller than 90 KB." });

    if (!string.IsNullOrWhiteSpace(request.DisplayName))
        user.DisplayName = request.DisplayName.Trim();
    user.Country = request.Country;
    user.StudyField = request.StudyField;
    user.Level = request.Level;
    user.Gpa = request.Gpa;
    user.HomeUniversity = request.HomeUniversity?.Trim();
    user.Intake = request.Intake?.Trim();
    user.Bio = request.Bio?.Trim();
    user.CommunityVisible = request.CommunityVisible ?? user.CommunityVisible;
    user.RoommateSearch = request.RoommateSearch ?? user.RoommateSearch;
    if (request.RemoveProfilePhoto)
        user.ProfilePhotoDataUrl = null;
    else if (request.ProfilePhotoDataUrl is not null)
    {
        _ = TryNormalizeProfilePhoto(request.ProfilePhotoDataUrl, out var normalizedPhotoDataUrl);
        user.ProfilePhotoDataUrl = normalizedPhotoDataUrl;
    }
    user.PreferredCountriesJson = System.Text.Json.JsonSerializer.Serialize(request.PreferredCountries);
    user.LanguagesJson = System.Text.Json.JsonSerializer.Serialize(request.Languages);
    await database.SaveChangesAsync();

    return Results.Ok(ToUserResponse(user));
}).RequireAuthorization();

app.MapGet("/api/reminders", async (ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    var reminders = await database.DeadlineReminders.AsNoTracking()
        .Where(item => item.UserId == userId.Value)
        .OrderBy(item => item.OpportunityId)
        .ToArrayAsync();
    return Results.Ok(reminders.Select(item => ToDeadlineReminderResponse(item, opportunities)));
}).RequireAuthorization();

app.MapPost("/api/reminders", async (SaveDeadlineReminderRequest request, ClaimsPrincipal principal,
    ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    var destination = opportunities.FirstOrDefault(item => item.Id == request.OpportunityId);
    if (destination is null)
        return Results.NotFound(new { message = "Choose a listed destination first." });
    if (!DateOnly.TryParse(destination.Deadline, out var deadline) || deadline < DateOnly.FromDateTime(DateTime.UtcNow))
        return Results.Conflict(new { message = "This listed deadline has passed or needs verification, so it cannot be reminded." });
    if (request.DaysBefore is not (1 or 3 or 7 or 14 or 30))
        return Results.BadRequest(new { message = "Choose a reminder window of 1, 3, 7, 14 or 30 days." });

    var reminder = await database.DeadlineReminders.SingleOrDefaultAsync(item =>
        item.UserId == userId.Value && item.OpportunityId == request.OpportunityId);
    if (reminder is null)
    {
        reminder = new DeadlineReminderEntity
        {
            UserId = userId.Value,
            OpportunityId = request.OpportunityId,
            DaysBefore = request.DaysBefore
        };
        database.DeadlineReminders.Add(reminder);
    }
    else
    {
        reminder.DaysBefore = request.DaysBefore;
        reminder.CreatedAt = DateTimeOffset.UtcNow;
        reminder.SentAt = null;
    }

    await database.SaveChangesAsync();
    return Results.Ok(ToDeadlineReminderResponse(reminder, opportunities));
}).RequireAuthorization();

app.MapDelete("/api/reminders/{id:int}", async (int id, ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    var reminder = await database.DeadlineReminders.SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId.Value);
    if (reminder is null) return Results.NotFound();
    database.DeadlineReminders.Remove(reminder);
    await database.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/community/plans", async (ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();

    var plans = await database.StudentPlans.AsNoTracking()
        .Where(item => item.UserId == userId.Value)
        .OrderByDescending(item => item.CreatedAt)
        .ToArrayAsync();
    return Results.Ok(plans.Select(item => new StudentPlanResponse(
        item.Id,
        item.OpportunityId,
        opportunities.FirstOrDefault(opportunity => opportunity.Id == item.OpportunityId)?.University ?? "University",
        item.Intake,
        item.CreatedAt,
        item.Stage,
        DeserializeStringArray(item.ChecklistJson),
        item.UpdatedAt)));
}).RequireAuthorization();

app.MapPost("/api/community/plans", async (CreateStudentPlanRequest request, ClaimsPrincipal principal,
    ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    var intake = request.Intake?.Trim();
    if (request.OpportunityId <= 0 || !opportunities.Any(item => item.Id == request.OpportunityId) ||
        string.IsNullOrWhiteSpace(intake) || intake.Length > 40)
        return Results.BadRequest(new { message = "Choose a destination and valid intake term." });

    var existing = await database.StudentPlans.SingleOrDefaultAsync(item => item.UserId == userId.Value &&
        item.OpportunityId == request.OpportunityId && item.Intake == intake);
    if (existing is not null)
        return Results.Ok(new StudentPlanResponse(existing.Id, existing.OpportunityId,
            opportunities.First(item => item.Id == existing.OpportunityId).University, existing.Intake, existing.CreatedAt,
            existing.Stage, DeserializeStringArray(existing.ChecklistJson), existing.UpdatedAt));

    var plan = new StudentPlanEntity { UserId = userId.Value, OpportunityId = request.OpportunityId, Intake = intake };
    database.StudentPlans.Add(plan);
    await database.SaveChangesAsync();
    return Results.Created($"/api/community/plans/{plan.Id}", new StudentPlanResponse(plan.Id, plan.OpportunityId,
        opportunities.First(item => item.Id == plan.OpportunityId).University, plan.Intake, plan.CreatedAt,
        plan.Stage, DeserializeStringArray(plan.ChecklistJson), plan.UpdatedAt));
}).RequireAuthorization();

app.MapPut("/api/community/plans/{id:int}", async (int id, UpdateStudentPlanRequest request,
    ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    var stage = request.Stage?.Trim();
    var allowedStages = new[] { "Considering", "Preparing", "Submitted", "Nominated", "Accepted", "Completed" };
    if (stage is null || !allowedStages.Contains(stage) || request.CompletedTasks is null || request.CompletedTasks.Length > 20 ||
        request.CompletedTasks.Any(task => string.IsNullOrWhiteSpace(task) || task.Length > 120))
        return Results.BadRequest(new { message = "Choose a valid application stage and checklist." });

    var plan = await database.StudentPlans.SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId.Value);
    if (plan is null) return Results.NotFound();
    plan.Stage = stage;
    plan.ChecklistJson = System.Text.Json.JsonSerializer.Serialize(request.CompletedTasks.Distinct(StringComparer.Ordinal).ToArray());
    plan.UpdatedAt = DateTimeOffset.UtcNow;
    await database.SaveChangesAsync();
    return Results.Ok(new StudentPlanResponse(plan.Id, plan.OpportunityId,
        opportunities.FirstOrDefault(item => item.Id == plan.OpportunityId)?.University ?? "University",
        plan.Intake, plan.CreatedAt, plan.Stage, DeserializeStringArray(plan.ChecklistJson), plan.UpdatedAt));
}).RequireAuthorization();

app.MapDelete("/api/community/plans/{id:int}", async (int id, ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    var plan = await database.StudentPlans.SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId.Value);
    if (plan is null)
        return Results.NotFound();
    database.StudentPlans.Remove(plan);
    await database.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/community/blocks", async (ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    var blockedUserIds = await database.CommunityBlocks.AsNoTracking()
        .Where(item => item.UserId == userId.Value)
        .Select(item => item.BlockedUserId)
        .ToArrayAsync();
    return Results.Ok(blockedUserIds);
}).RequireAuthorization();

app.MapPost("/api/community/users/{id:int}/block", async (int id, ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    if (id == userId.Value || !await database.Users.AnyAsync(item => item.Id == id && item.EmailVerified))
        return Results.BadRequest(new { message = "Choose a different verified student." });

    if (!await database.CommunityBlocks.AnyAsync(item => item.UserId == userId.Value && item.BlockedUserId == id))
    {
        database.CommunityBlocks.Add(new CommunityBlockEntity { UserId = userId.Value, BlockedUserId = id });
        await database.SaveChangesAsync();
    }
    return Results.NoContent();
}).RequireAuthorization();

app.MapDelete("/api/community/users/{id:int}/block", async (int id, ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    var block = await database.CommunityBlocks.SingleOrDefaultAsync(item => item.UserId == userId.Value && item.BlockedUserId == id);
    if (block is null) return Results.NotFound();
    database.CommunityBlocks.Remove(block);
    await database.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/community/reports", async (ReportCommunityUserRequest request, ClaimsPrincipal principal,
    ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    var reason = request.Reason?.Trim();
    if (request.ReportedUserId == userId.Value || string.IsNullOrWhiteSpace(reason) || reason.Length > 500 ||
        !await database.Users.AnyAsync(item => item.Id == request.ReportedUserId && item.EmailVerified))
        return Results.BadRequest(new { message = "Choose a student and enter a report reason under 500 characters." });
    if (request.ConversationId is { } conversationId &&
        !await database.Conversations.AnyAsync(item => item.Id == conversationId &&
            (item.UserAId == userId.Value && item.UserBId == request.ReportedUserId ||
             item.UserBId == userId.Value && item.UserAId == request.ReportedUserId)))
        return Results.BadRequest(new { message = "The selected conversation does not match this report." });

    var report = new CommunityReportEntity
    {
        ReporterUserId = userId.Value,
        ReportedUserId = request.ReportedUserId,
        ConversationId = request.ConversationId,
        Reason = reason
    };
    database.CommunityReports.Add(report);
    await database.SaveChangesAsync();
    app.Logger.LogWarning("Community report {ReportId} submitted by user {ReporterId} for user {ReportedId}.",
        report.Id, userId.Value, request.ReportedUserId);
    return Results.Accepted();
}).RequireAuthorization().RequireRateLimiting("community-message");

app.MapGet("/api/community/students", async (int opportunityId, string intake, bool? roommatesOnly, ClaimsPrincipal principal,
    ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    if (!opportunities.Any(item => item.Id == opportunityId) || string.IsNullOrWhiteSpace(intake) || intake.Length > 40)
        return Results.BadRequest(new { message = "Choose a destination and intake term." });

    var blockedRows = await database.CommunityBlocks.AsNoTracking()
        .Where(item => item.UserId == userId.Value || item.BlockedUserId == userId.Value)
        .Select(item => new { item.UserId, item.BlockedUserId })
        .ToArrayAsync();
    var excludedIds = blockedRows.Select(item => item.UserId == userId.Value ? item.BlockedUserId : item.UserId).ToArray();
    var matchingIds = await database.StudentPlans.AsNoTracking()
        .Where(item => item.OpportunityId == opportunityId && item.Intake == intake && item.UserId != userId.Value)
        .Where(item => !excludedIds.Contains(item.UserId))
        .Select(item => item.UserId)
        .Distinct()
        .ToArrayAsync();
    var students = await database.Users.AsNoTracking()
        .Where(item => matchingIds.Contains(item.Id) && item.EmailVerified && item.CommunityVisible && (roommatesOnly != true || item.RoommateSearch))
        .OrderBy(item => item.DisplayName)
        .Take(30)
        .Select(item => new StudentCardResponse(item.Id, item.DisplayName, item.HomeUniversity, item.StudyField,
            item.Level, item.Intake, item.Bio, item.ProfilePhotoDataUrl, item.RoommateSearch))
        .ToArrayAsync();
    return Results.Ok(students);
}).RequireAuthorization();

app.MapGet("/api/community/conversations", async (ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();

    var conversations = await database.Conversations.AsNoTracking()
        .Where(item => item.UserAId == userId.Value || item.UserBId == userId.Value)
        .OrderByDescending(item => item.CreatedAt)
        .Take(50)
        .ToArrayAsync();
    var conversationIds = conversations.Select(item => item.Id).ToArray();
    var otherUserIds = conversations.Select(item => item.UserAId == userId.Value ? item.UserBId : item.UserAId).Distinct().ToArray();
    var otherUsers = await database.Users.AsNoTracking().Where(item => otherUserIds.Contains(item.Id))
        .ToDictionaryAsync(item => item.Id);
    var latestMessageIds = await database.Messages.AsNoTracking()
        .Where(item => conversationIds.Contains(item.ConversationId))
        .GroupBy(item => item.ConversationId)
        .Select(group => new { ConversationId = group.Key, MessageId = group.Max(item => item.Id) })
        .ToArrayAsync();
    var messageIds = latestMessageIds.Select(item => item.MessageId).ToArray();
    var latestMessages = await database.Messages.AsNoTracking().Where(item => messageIds.Contains(item.Id))
        .ToDictionaryAsync(item => item.ConversationId, item => new MessagePreview(item.Content, item.CreatedAt));
    var blockedRows = await database.CommunityBlocks.AsNoTracking()
        .Where(item => item.UserId == userId.Value && otherUserIds.Contains(item.BlockedUserId) ||
            item.BlockedUserId == userId.Value && otherUserIds.Contains(item.UserId))
        .ToArrayAsync();
    var unreadRows = await (
        from message in database.Messages.AsNoTracking()
        join state in database.ConversationReadStates.AsNoTracking().Where(item => item.UserId == userId.Value)
            on message.ConversationId equals state.ConversationId into states
        from state in states.DefaultIfEmpty()
        where conversationIds.Contains(message.ConversationId) && message.SenderUserId != userId.Value &&
            (state == null || message.Id > state.LastReadMessageId)
        group message by message.ConversationId into messages
        select new { ConversationId = messages.Key, Count = messages.Count() })
        .ToArrayAsync();
    var unreadCounts = unreadRows.ToDictionary(item => item.ConversationId, item => item.Count);
    var summaries = new List<ConversationSummaryResponse>(conversations.Length);
    foreach (var conversation in conversations)
    {
        var otherUserId = conversation.UserAId == userId.Value ? conversation.UserBId : conversation.UserAId;
        if (!otherUsers.TryGetValue(otherUserId, out var otherUser)) continue;
        latestMessages.TryGetValue(conversation.Id, out var lastMessage);
        var unreadCount = unreadCounts.GetValueOrDefault(conversation.Id);
        var isBlocked = blockedRows.Any(item =>
            (item.UserId == userId.Value && item.BlockedUserId == otherUserId) ||
            (item.UserId == otherUserId && item.BlockedUserId == userId.Value));
        summaries.Add(new ConversationSummaryResponse(conversation.Id, otherUser.Id, otherUser.DisplayName,
            otherUser.CommunityVisible ? otherUser.HomeUniversity : null,
            otherUser.CommunityVisible ? otherUser.ProfilePhotoDataUrl : null,
            opportunities.FirstOrDefault(item => item.Id == conversation.OpportunityId)?.University ?? "Erasmus exchange",
            conversation.Intake, lastMessage?.Content, lastMessage?.CreatedAt, conversation.CreatedAt, unreadCount, isBlocked));
    }
    return Results.Ok(summaries);
}).RequireAuthorization();

app.MapPost("/api/community/conversations", async (StartConversationRequest request, ClaimsPrincipal principal,
    ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    var recipient = await database.Users.SingleOrDefaultAsync(item => item.Id == request.RecipientUserId &&
        item.EmailVerified && item.CommunityVisible);
    var intake = request.Intake?.Trim();
    if (recipient is null || recipient.Id == userId.Value || !opportunities.Any(item => item.Id == request.OpportunityId) ||
        string.IsNullOrWhiteSpace(intake) || intake.Length > 40)
        return Results.BadRequest(new { message = "This student is not available for this exchange plan." });
    if (await database.CommunityBlocks.AnyAsync(item =>
        (item.UserId == userId.Value && item.BlockedUserId == recipient.Id) ||
        (item.UserId == recipient.Id && item.BlockedUserId == userId.Value)))
        return Results.Conflict(new { message = "This conversation is unavailable." });

    var senderHasPlan = await database.StudentPlans.AnyAsync(item => item.UserId == userId.Value &&
        item.OpportunityId == request.OpportunityId && item.Intake == intake);
    var recipientHasPlan = await database.StudentPlans.AnyAsync(item => item.UserId == recipient.Id &&
        item.OpportunityId == request.OpportunityId && item.Intake == intake);
    if (!senderHasPlan || !recipientHasPlan)
        return Results.Forbid();

    var userAId = Math.Min(userId.Value, recipient.Id);
    var userBId = Math.Max(userId.Value, recipient.Id);
    var conversation = await database.Conversations.SingleOrDefaultAsync(item => item.UserAId == userAId &&
        item.UserBId == userBId && item.OpportunityId == request.OpportunityId && item.Intake == intake);
    if (conversation is null)
    {
        conversation = new ConversationEntity
        {
            UserAId = userAId,
            UserBId = userBId,
            OpportunityId = request.OpportunityId,
            Intake = intake
        };
        database.Conversations.Add(conversation);
        await database.SaveChangesAsync();
    }
    return Results.Ok(new { conversationId = conversation.Id });
}).RequireAuthorization();

app.MapGet("/api/community/conversations/{id:int}/messages", async (int id, ClaimsPrincipal principal,
    ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    var conversation = await database.Conversations.AsNoTracking()
        .SingleOrDefaultAsync(item => item.Id == id && (item.UserAId == userId.Value || item.UserBId == userId.Value));
    if (conversation is null)
        return Results.NotFound();

    var messages = await database.Messages.AsNoTracking()
        .Where(item => item.ConversationId == id)
        .OrderByDescending(item => item.Id)
        .Take(50)
        .OrderBy(item => item.Id)
        .Select(item => new MessageResponse(item.Id, item.SenderUserId, item.Content, item.CreatedAt))
        .ToArrayAsync();
    var lastReadMessageId = messages.LastOrDefault()?.Id ?? 0;
    var readState = await database.ConversationReadStates.SingleOrDefaultAsync(item =>
        item.ConversationId == id && item.UserId == userId.Value);
    if (readState is null)
        database.ConversationReadStates.Add(new ConversationReadStateEntity
        {
            ConversationId = id,
            UserId = userId.Value,
            LastReadMessageId = lastReadMessageId
        });
    else
    {
        readState.LastReadMessageId = Math.Max(readState.LastReadMessageId, lastReadMessageId);
        readState.UpdatedAt = DateTimeOffset.UtcNow;
    }
    await database.SaveChangesAsync();
    return Results.Ok(messages);
}).RequireAuthorization();

app.MapPost("/api/community/conversations/{id:int}/messages", async (int id, SendMessageRequest request,
    ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    var conversation = await database.Conversations
        .SingleOrDefaultAsync(item => item.Id == id && (item.UserAId == userId.Value || item.UserBId == userId.Value));
    if (conversation is null)
        return Results.NotFound();
    var otherUserId = conversation.UserAId == userId.Value ? conversation.UserBId : conversation.UserAId;
    if (await database.CommunityBlocks.AnyAsync(item =>
        (item.UserId == userId.Value && item.BlockedUserId == otherUserId) ||
        (item.UserId == otherUserId && item.BlockedUserId == userId.Value)))
        return Results.Conflict(new { message = "This conversation is unavailable." });
    var content = request.Content?.Trim();
    if (string.IsNullOrWhiteSpace(content) || content.Length > 2000)
        return Results.BadRequest(new { message = "Messages must contain between 1 and 2,000 characters." });

    var message = new MessageEntity { ConversationId = id, SenderUserId = userId.Value, Content = content };
    database.Messages.Add(message);
    await database.SaveChangesAsync();
    return Results.Created($"/api/community/conversations/{id}/messages/{message.Id}",
        new MessageResponse(message.Id, message.SenderUserId, message.Content, message.CreatedAt));
}).RequireAuthorization().RequireRateLimiting("community-message");

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "ErasmusWithUs.Api" }));

app.MapPost("/api/notifications/devices", async (RegisterPushDeviceRequest request,
    ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || string.IsNullOrWhiteSpace(request.ExpoPushToken) ||
        !request.ExpoPushToken.StartsWith("ExponentPushToken[", StringComparison.Ordinal))
        return Results.BadRequest(new { message = "A valid Expo push token is required." });

    var token = request.ExpoPushToken.Trim();
    var device = await database.PushDevices.SingleOrDefaultAsync(item =>
        item.UserId == userId.Value && item.ExpoPushToken == token);
    if (device is null)
    {
        device = new PushDeviceEntity
        {
            UserId = userId.Value,
            ExpoPushToken = token,
            Platform = request.Platform?.Trim() is { Length: > 0 } platform ? platform : "unknown"
        };
        database.PushDevices.Add(device);
    }
    else
    {
        device.Platform = request.Platform?.Trim() is { Length: > 0 } platform ? platform : device.Platform;
        device.UpdatedAt = DateTimeOffset.UtcNow;
    }

    await database.SaveChangesAsync();
    return Results.Ok(new { registered = true });
}).RequireAuthorization();

app.MapDelete("/api/notifications/devices", async (string? expoPushToken,
    ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || string.IsNullOrWhiteSpace(expoPushToken))
        return Results.BadRequest(new { message = "A push token is required." });

    var device = await database.PushDevices.SingleOrDefaultAsync(item =>
        item.UserId == userId.Value && item.ExpoPushToken == expoPushToken.Trim());
    if (device is not null)
    {
        database.PushDevices.Remove(device);
        await database.SaveChangesAsync();
    }
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/assistant/recommend", async (AssistantRequest request, IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
        return Results.BadRequest(new { message = "Tell the assistant what you want to compare." });

    var endpoint = configuration["AI:Endpoint"];
    var apiKey = configuration["AI:ApiKey"];
    var model = configuration["AI:Model"] ?? "gpt-4o-mini";
    if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
        return Results.Problem("The AI assistant is not configured. Set AI:Endpoint, AI:ApiKey and AI:Model.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    var payload = new
    {
        model,
        temperature = 0.2,
        messages = new[]
        {
            new { role = "system", content = "You are Erasmus With Us assistant. Give concise, practical advice. Never invent deadlines, grants, or eligibility rules. Ask the user to verify official university sources." },
            new { role = "user", content = request.Message }
        }
    };
    using var response = await client.PostAsJsonAsync(endpoint.TrimEnd('/') + "/chat/completions", payload, cancellationToken);
    var body = await response.Content.ReadFromJsonAsync<AiCompletionResponse>(cancellationToken: cancellationToken);
    var answer = body?.Choices?.FirstOrDefault()?.Message?.Content;
    if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(answer))
        return Results.Problem("The AI assistant provider did not return a usable response.",
            statusCode: StatusCodes.Status502BadGateway);

    return Results.Ok(new { answer = answer.Trim() });
}).RequireRateLimiting("auth");

app.MapGet("/api/opportunities", (string? search, string? country, string? studyField, string? level) =>
{
    var results = opportunities.AsEnumerable();

    if (!string.IsNullOrWhiteSpace(search))
    {
        results = results.Where(item =>
            item.University.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            item.City.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            item.StudyFields.Any(field => field.Contains(search, StringComparison.OrdinalIgnoreCase)));
    }

    if (!string.IsNullOrWhiteSpace(country))
    {
        results = results.Where(item => item.Country.Equals(country, StringComparison.OrdinalIgnoreCase));
    }

    if (!string.IsNullOrWhiteSpace(studyField))
    {
        results = results.Where(item => item.StudyFields.Any(field =>
            field.Equals(studyField, StringComparison.OrdinalIgnoreCase)));
    }

    if (!string.IsNullOrWhiteSpace(level))
    {
        results = results.Where(item => item.Levels.Any(itemLevel =>
            itemLevel.Equals(level, StringComparison.OrdinalIgnoreCase)));
    }

    return Results.Ok(results.ToArray());
});

app.MapGet("/api/opportunities/{id:int}", (int id) =>
    opportunities.FirstOrDefault(item => item.Id == id) is { } opportunity
        ? Results.Ok(opportunity)
        : Results.NotFound());

app.MapGet("/api/opportunities/{id:int}/experiences", async (int id, ErasmusDbContext database) =>
{
    if (!opportunities.Any(item => item.Id == id)) return Results.NotFound();
    var ratingStats = await database.StudentExperiences.AsNoTracking()
        .Where(item => item.OpportunityId == id)
        .GroupBy(item => item.OpportunityId)
        .Select(group => new { Count = group.Count(), AverageRating = group.Average(item => (double)item.Rating) })
        .SingleOrDefaultAsync();
    var experiences = await database.StudentExperiences.AsNoTracking()
        .Where(item => item.OpportunityId == id)
        .OrderByDescending(item => item.CreatedAt)
        .Take(50)
        .Join(database.Users.AsNoTracking(), experience => experience.UserId, user => user.Id,
            (experience, user) => new StudentExperienceResponse(
                experience.Id,
                user.CommunityVisible ? user.DisplayName : "Erasmus With Us student",
                experience.Rating,
                experience.ExperienceType,
                experience.Intake,
                experience.Content,
                experience.CreatedAt))
        .ToArrayAsync();
    return Results.Ok(new ExperienceSummaryResponse(
        ratingStats is null ? 0 : Math.Round(ratingStats.AverageRating, 1),
        ratingStats?.Count ?? 0,
        experiences));
});

app.MapPost("/api/opportunities/{id:int}/experiences", async (int id, SaveStudentExperienceRequest request,
    ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    if (!opportunities.Any(item => item.Id == id)) return Results.NotFound();

    var content = request.Content?.Trim();
    var intake = request.Intake?.Trim();
    if (request.Rating is < 1 or > 5 || string.IsNullOrWhiteSpace(content) || content.Length is < 20 or > 1500 ||
        string.IsNullOrWhiteSpace(intake) || intake.Length > 40 ||
        request.ExperienceType is not ("Alumni" or "Planning"))
        return Results.BadRequest(new { message = "Add a 1–5 rating, select your experience type and intake, and write 20–1,500 characters." });

    var experience = await database.StudentExperiences.SingleOrDefaultAsync(item => item.UserId == userId.Value && item.OpportunityId == id);
    if (experience is null)
    {
        experience = new StudentExperienceEntity { UserId = userId.Value, OpportunityId = id, Rating = request.Rating,
            ExperienceType = request.ExperienceType, Intake = intake, Content = content };
        database.StudentExperiences.Add(experience);
    }
    else
    {
        experience.Rating = request.Rating;
        experience.ExperienceType = request.ExperienceType;
        experience.Intake = intake;
        experience.Content = content;
        experience.CreatedAt = DateTimeOffset.UtcNow;
    }
    await database.SaveChangesAsync();
    var user = await database.Users.AsNoTracking().SingleAsync(item => item.Id == userId.Value);
    return Results.Ok(new StudentExperienceResponse(experience.Id,
        user.CommunityVisible ? user.DisplayName : "Erasmus With Us student", experience.Rating,
        experience.ExperienceType, experience.Intake, experience.Content, experience.CreatedAt));
}).RequireAuthorization().RequireRateLimiting("community-message");

app.MapGet("/api/opportunities/{id:int}/questions", async (int id, ErasmusDbContext database) =>
{
    if (!opportunities.Any(item => item.Id == id)) return Results.NotFound();
    var questions = await database.OpportunityQuestions.AsNoTracking()
        .Where(item => item.OpportunityId == id)
        .OrderByDescending(item => item.CreatedAt)
        .Take(40)
        .ToArrayAsync();
    var questionIds = questions.Select(item => item.Id).ToArray();
    var answerRows = await database.OpportunityAnswers.AsNoTracking()
        .Where(item => questionIds.Contains(item.QuestionId))
        .OrderBy(item => item.CreatedAt)
        .Join(database.Users.AsNoTracking(), answer => answer.UserId, user => user.Id,
            (answer, user) => new
            {
                answer.QuestionId,
                Response = new OpportunityAnswerResponse(answer.Id,
                    user.CommunityVisible ? user.DisplayName : "Erasmus With Us student",
                    answer.ExperienceType, answer.Content, answer.CreatedAt)
            })
        .ToArrayAsync();
    var answersByQuestion = answerRows.GroupBy(item => item.QuestionId)
        .ToDictionary(group => group.Key, group => group.Select(item => item.Response).ToArray());
    var displayNames = await database.Users.AsNoTracking()
        .Where(item => questions.Select(question => question.UserId).Contains(item.Id))
        .Select(item => new { item.Id, item.DisplayName, item.CommunityVisible })
        .ToDictionaryAsync(item => item.Id);
    return Results.Ok(questions.Select(question => new OpportunityQuestionResponse(
        question.Id,
        displayNames.TryGetValue(question.UserId, out var asker) && asker.CommunityVisible
            ? asker.DisplayName : "Erasmus With Us student",
        question.Content,
        question.CreatedAt,
        answersByQuestion.GetValueOrDefault(question.Id, []))));
});

app.MapPost("/api/opportunities/{id:int}/questions", async (int id, SaveOpportunityQuestionRequest request,
    ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    if (!opportunities.Any(item => item.Id == id)) return Results.NotFound();
    var content = request.Content?.Trim();
    if (string.IsNullOrWhiteSpace(content) || content.Length is < 10 or > 500)
        return Results.BadRequest(new { message = "Write a question between 10 and 500 characters." });
    var question = new OpportunityQuestionEntity { UserId = userId.Value, OpportunityId = id, Content = content };
    database.OpportunityQuestions.Add(question);
    await database.SaveChangesAsync();
    var user = await database.Users.AsNoTracking().SingleAsync(item => item.Id == userId.Value);
    return Results.Created($"/api/opportunities/{id}/questions", new OpportunityQuestionResponse(
        question.Id, user.CommunityVisible ? user.DisplayName : "Erasmus With Us student", question.Content,
        question.CreatedAt, []));
}).RequireAuthorization().RequireRateLimiting("community-message");

app.MapPost("/api/questions/{questionId:int}/answers", async (int questionId, SaveOpportunityAnswerRequest request,
    ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    var question = await database.OpportunityQuestions.FindAsync(questionId);
    if (question is null) return Results.NotFound();
    var content = request.Content?.Trim();
    if (string.IsNullOrWhiteSpace(content) || content.Length is < 10 or > 1200 ||
        request.ExperienceType is not ("Alumni" or "Planning"))
        return Results.BadRequest(new { message = "Choose your experience type and write an answer between 10 and 1,200 characters." });
    var answer = await database.OpportunityAnswers.SingleOrDefaultAsync(item =>
        item.QuestionId == questionId && item.UserId == userId.Value);
    if (answer is null)
    {
        answer = new OpportunityAnswerEntity { QuestionId = questionId, UserId = userId.Value,
            Content = content, ExperienceType = request.ExperienceType };
        database.OpportunityAnswers.Add(answer);
    }
    else
    {
        answer.Content = content;
        answer.ExperienceType = request.ExperienceType;
        answer.CreatedAt = DateTimeOffset.UtcNow;
    }
    await database.SaveChangesAsync();
    var user = await database.Users.AsNoTracking().SingleAsync(item => item.Id == userId.Value);
    return Results.Ok(new OpportunityAnswerResponse(answer.Id,
        user.CommunityVisible ? user.DisplayName : "Erasmus With Us student", answer.ExperienceType, answer.Content, answer.CreatedAt));
}).RequireAuthorization().RequireRateLimiting("community-message");

app.MapPost("/api/opportunities/{id:int}/corrections", async (int id, SubmitCatalogCorrectionRequest request,
    ClaimsPrincipal principal, ErasmusDbContext database) =>
{
    var userId = GetUserId(principal);
    if (userId is null || !await database.Users.AnyAsync(item => item.Id == userId && item.EmailVerified))
        return Results.Unauthorized();
    if (!opportunities.Any(item => item.Id == id)) return Results.NotFound();
    var supportedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "University", "City", "Country", "StudyFields", "Levels", "Languages", "MinimumGpa", "Deadline", "MonthlyGrant", "SourceUrl" };
    var fields = request.Fields;
    var note = request.Note?.Trim();
    if (fields is null || fields.Count is < 1 or > 8 || fields.Any(field =>
            !supportedFields.Contains(field.Key) || string.IsNullOrWhiteSpace(field.Value) || field.Value.Length > 300) ||
        string.IsNullOrWhiteSpace(note) || note.Length is < 10 or > 700)
        return Results.BadRequest(new { message = "Select valid catalogue fields and explain the correction in 10–700 characters." });
    var sourceUrl = request.SourceUrl?.Trim();
    if (sourceUrl is not null && (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri) || sourceUri.Scheme != Uri.UriSchemeHttps))
        return Results.BadRequest(new { message = "The supporting source must be an HTTPS link." });
    var correction = new CatalogCorrectionEntity
    {
        UserId = userId.Value,
        OpportunityId = id,
        FieldsJson = System.Text.Json.JsonSerializer.Serialize(fields),
        SourceUrl = sourceUrl,
        Note = note
    };
    database.CatalogCorrections.Add(correction);
    await database.SaveChangesAsync();
    return Results.Accepted($"/api/opportunities/{id}/corrections", new { correction.Id, correction.Status, correction.CreatedAt });
}).RequireAuthorization().RequireRateLimiting("community-message");

app.MapGet("/api/catalog/corrections/review", async (HttpRequest request, IConfiguration configuration,
    ErasmusDbContext database) =>
{
    if (!HasCatalogReviewAccess(request, configuration)) return Results.Unauthorized();
    var items = await database.CatalogCorrections.AsNoTracking()
        .Where(item => item.Status == "Pending")
        .OrderBy(item => item.CreatedAt)
        .Join(database.Users.AsNoTracking(), correction => correction.UserId, user => user.Id,
            (correction, user) => new { correction, user.DisplayName })
        .ToArrayAsync();
    return Results.Ok(items.Select(item => new CatalogCorrectionResponse(item.correction.Id,
        item.correction.OpportunityId,
        opportunities.FirstOrDefault(opportunity => opportunity.Id == item.correction.OpportunityId)?.University ?? "University",
        item.DisplayName,
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(item.correction.FieldsJson) ?? [],
        item.correction.SourceUrl, item.correction.Note, item.correction.Status, item.correction.ReviewerNote,
        item.correction.CreatedAt, item.correction.ReviewedAt)));
});

app.MapPut("/api/catalog/corrections/{id:int}/review", async (int id, ReviewCatalogCorrectionRequest request,
    HttpRequest httpRequest, IConfiguration configuration, ErasmusDbContext database) =>
{
    if (!HasCatalogReviewAccess(httpRequest, configuration)) return Results.Unauthorized();
    if (request.Status is not ("Accepted" or "Rejected" or "NeedsMoreInfo") || request.ReviewerNote?.Length > 500)
        return Results.BadRequest(new { message = "Choose Accepted, Rejected or NeedsMoreInfo and keep the review note under 500 characters." });
    var correction = await database.CatalogCorrections.FindAsync(id);
    if (correction is null) return Results.NotFound();
    if (correction.Status != "Pending") return Results.Conflict(new { message = "This suggestion has already been reviewed." });

    if (request.Status == "Accepted")
    {
        if (string.IsNullOrWhiteSpace(correction.SourceUrl))
            return Results.BadRequest(new { message = "Add an official HTTPS source before accepting this correction." });
        var entity = await database.Opportunities.FindAsync(correction.OpportunityId);
        if (entity is null) return Results.NotFound();
        var fields = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(correction.FieldsJson) ?? [];
        foreach (var (key, rawValue) in fields)
        {
            var value = rawValue?.Trim() ?? string.Empty;
            switch (key.ToLowerInvariant())
            {
                case "university" when value.Length is > 0 and <= 200: entity.University = value; break;
                case "city" when value.Length is > 0 and <= 100: entity.City = value; break;
                case "country" when value.Length is > 0 and <= 100: entity.Country = value; break;
                case "studyfields": entity.StudyFields = ParseCatalogList(value); break;
                case "levels": entity.Levels = ParseCatalogList(value); break;
                case "languages": entity.Languages = ParseCatalogList(value); break;
                case "minimumgpa" when double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var gpa) && gpa is >= 0 and <= 4:
                    entity.MinimumGpa = gpa; break;
                case "deadline" when DateOnly.TryParse(value, out var deadline): entity.Deadline = deadline.ToString("yyyy-MM-dd"); break;
                case "monthlygrant" when int.TryParse(value, out var grant) && grant is >= 0 and <= 2000:
                    entity.MonthlyGrant = grant; break;
                case "sourceurl" when Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps:
                    entity.SourceUrl = value; break;
                default:
                    return Results.BadRequest(new { message = $"The proposed value for {key} is invalid. Review it before accepting." });
            }
        }
        if (Uri.TryCreate(correction.SourceUrl, UriKind.Absolute, out var suppliedSource) && suppliedSource.Scheme == Uri.UriSchemeHttps)
            entity.SourceUrl = correction.SourceUrl;
        entity.LastVerifiedAt = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        entity.VerificationStatus = "Verified";
    }

    correction.Status = request.Status;
    correction.ReviewerNote = request.ReviewerNote?.Trim();
    correction.ReviewedAt = DateTimeOffset.UtcNow;
    await database.SaveChangesAsync();
    if (request.Status == "Accepted")
        opportunities = (await database.Opportunities.AsNoTracking().ToArrayAsync()).Select(ToOpportunity).ToArray();
    return Results.Ok(new { correction.Id, correction.Status, correction.ReviewerNote, correction.ReviewedAt });
});

app.MapPost("/api/matching", (StudentProfile profile) =>
{
    var matches = opportunities
        .Select(opportunity => new MatchResult(
            opportunity,
            CalculateMatchScore(profile, opportunity),
            BuildMatchReasons(profile, opportunity),
            BuildMatchBreakdown(profile, opportunity),
            BuildEligibilityAssessment(profile, opportunity)))
        .OrderByDescending(result => result.Score)
        .ToArray();

    return Results.Ok(matches);
});

app.MapGet("/api/sources/status", () => Results.Ok(opportunities
    .Select(item => new
    {
        item.Id,
        item.University,
        item.SourceUrl,
        item.LastVerifiedAt,
        item.VerificationStatus
    })));

app.MapGet("/api/catalog/countries", () => Results.Ok(opportunities
    .Select(item => item.Country)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .OrderBy(country => country)));

app.MapGet("/api/catalog/study-fields", () => Results.Ok(opportunities
    .SelectMany(item => item.StudyFields)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .OrderBy(field => field)));

app.MapGet("/api/catalog/levels", () => Results.Ok(opportunities
    .SelectMany(item => item.Levels)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .OrderBy(level => level)));

app.Run();

static int CalculateMatchScore(StudentProfile profile, ErasmusOpportunity opportunity)
{
    var factors = BuildMatchBreakdown(profile, opportunity);
    var earned = factors.Sum(item => item.Points);
    var possible = factors.Sum(item => item.MaxPoints);
    return possible == 0 ? 0 : Math.Clamp((int)Math.Round(earned / (double)possible * 100), 0, 100);
}

static EligibilityAssessment BuildEligibilityAssessment(StudentProfile profile, ErasmusOpportunity opportunity)
{
    var reasons = new List<string>();
    var hasField = !string.IsNullOrWhiteSpace(profile.StudyField);
    var hasLevel = !string.IsNullOrWhiteSpace(profile.Level);
    var hasGpa = profile.Gpa > 0;
    var meetsPublishedRequirements = true;

    if (hasField && !opportunity.StudyFields.Contains(profile.StudyField!, StringComparer.OrdinalIgnoreCase))
    {
        meetsPublishedRequirements = false;
        reasons.Add("Your study field is not listed for this destination.");
    }
    else if (hasField)
        reasons.Add("Your study field appears in the published list.");

    if (hasLevel && !opportunity.Levels.Contains(profile.Level!, StringComparer.OrdinalIgnoreCase))
    {
        meetsPublishedRequirements = false;
        reasons.Add("Your degree level is not listed for this destination.");
    }
    else if (hasLevel)
        reasons.Add("Your degree level appears in the published list.");

    if (hasGpa && profile.Gpa < opportunity.MinimumGpa)
    {
        meetsPublishedRequirements = false;
        reasons.Add($"Your GPA is below the listed minimum of {opportunity.MinimumGpa:0.0}.");
    }
    else if (hasGpa)
        reasons.Add("Your GPA meets the listed minimum.");

    var deadlineIsCurrent = DateOnly.TryParse(opportunity.Deadline, out var deadline) &&
        deadline >= DateOnly.FromDateTime(DateTime.UtcNow);
    if (!deadlineIsCurrent)
    {
        meetsPublishedRequirements = false;
        reasons.Add("The listed application deadline has passed or needs verification.");
    }

    var hasEnoughProfileData = hasField && hasLevel && hasGpa;
    if (!hasField) reasons.Add("Add your study field to check the listed subject requirements.");
    if (!hasLevel) reasons.Add("Add your degree level to check the listed level requirements.");
    if (!hasGpa) reasons.Add("Add your GPA to compare it with the listed minimum.");
    reasons.Add("The bilateral agreement and nomination must still be confirmed by your home university.");

    return new EligibilityAssessment(meetsPublishedRequirements, hasEnoughProfileData, false, reasons.ToArray());
}

static string[] BuildMatchReasons(StudentProfile profile, ErasmusOpportunity opportunity)
{
    var reasons = new List<string>();

    if (!string.IsNullOrWhiteSpace(profile.StudyField) &&
        opportunity.StudyFields.Contains(profile.StudyField, StringComparer.OrdinalIgnoreCase))
        reasons.Add("Your study field matches this destination.");

    if (profile.Gpa >= opportunity.MinimumGpa)
        reasons.Add("Your GPA meets the minimum requirement.");

    if (profile.Languages.Intersect(opportunity.Languages, StringComparer.OrdinalIgnoreCase).Any())
        reasons.Add("Your language profile fits this campus.");

    if (profile.Budget > 0 && opportunity.StudentBudgetEstimate <= profile.Budget)
        reasons.Add("The estimated monthly cost fits your budget.");

    var likedMatches = GetPreferenceTags(profile.Likes).Intersect(GetOpportunityTags(opportunity), StringComparer.OrdinalIgnoreCase).ToArray();
    if (likedMatches.Length > 0)
        reasons.Add($"This option matches your preference for {likedMatches[0]}.");

    return reasons.ToArray();
}

static MatchFactor[] BuildMatchBreakdown(StudentProfile profile, ErasmusOpportunity opportunity)
{
    var homeCountryPoints = !string.IsNullOrWhiteSpace(profile.Country) && opportunity.Country.Equals(profile.Country, StringComparison.OrdinalIgnoreCase) ? 10 : 0;
    var countryPoints = profile.PreferredCountries.Contains(opportunity.Country, StringComparer.OrdinalIgnoreCase) ? 25 : 0;
    var fieldPoints = !string.IsNullOrWhiteSpace(profile.StudyField) && opportunity.StudyFields.Contains(profile.StudyField, StringComparer.OrdinalIgnoreCase) ? 30 : 0;
    var levelPoints = !string.IsNullOrWhiteSpace(profile.Level) && opportunity.Levels.Contains(profile.Level, StringComparer.OrdinalIgnoreCase) ? 15 : 0;
    var gpaPoints = profile.Gpa >= opportunity.MinimumGpa ? 20 : (int)Math.Max(0, (profile.Gpa / Math.Max(opportunity.MinimumGpa, 0.1)) * 10 - 10);
    var languagePoints = profile.Languages.Intersect(opportunity.Languages, StringComparer.OrdinalIgnoreCase).Any() ? 15 : 0;
    var budgetPoints = profile.Budget > 0 && opportunity.StudentBudgetEstimate <= profile.Budget ? 10 : 0;
    var opportunityTags = GetOpportunityTags(opportunity);
    var likedMatches = GetPreferenceTags(profile.Likes).Intersect(opportunityTags, StringComparer.OrdinalIgnoreCase).Count();
    var dislikedMatches = GetPreferenceTags(profile.Dislikes).Intersect(opportunityTags, StringComparer.OrdinalIgnoreCase).Count();
    var preferencePoints = Math.Clamp(likedMatches * 8 - dislikedMatches * 12, -24, 24);

    return
    [
        new MatchFactor("Home country", homeCountryPoints, 10),
        new MatchFactor("Preferred country", countryPoints, 25),
        new MatchFactor("Study field", fieldPoints, 30),
        new MatchFactor("Degree level", levelPoints, 15),
        new MatchFactor("GPA", gpaPoints, 20),
        new MatchFactor("Language", languagePoints, 15),
        new MatchFactor("Grant / budget", budgetPoints, 10),
        new MatchFactor("Lifestyle choices", preferencePoints, 24)
    ];
}

static HashSet<string> GetOpportunityTags(ErasmusOpportunity opportunity)
{
    var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    if (opportunity.StudyFields.Any(field => field.Contains("Computer", StringComparison.OrdinalIgnoreCase) || field.Contains("Engineering", StringComparison.OrdinalIgnoreCase)))
        tags.Add("tech");
    if (opportunity.StudyFields.Any(field => field.Contains("Design", StringComparison.OrdinalIgnoreCase) || field.Contains("Arts", StringComparison.OrdinalIgnoreCase)))
        tags.Add("creative");
    if (opportunity.MonthlyGrant >= 500)
        tags.Add("strong grant");
    if (opportunity.MonthlyGrant >= 490)
        tags.Add("budget friendly");

    if (opportunity.Country is "Finland" or "Sweden" or "Norway" or "Denmark" or "Estonia" or "Czechia" or "Poland")
    {
        tags.Add("cold");
        tags.Add("nordic");
    }

    if (opportunity.Country is "Spain" or "Portugal" or "Italy" or "Greece")
    {
        tags.Add("warm");
        tags.Add("social");
    }

    if (opportunity.City is "Berlin" or "Amsterdam" or "Paris" or "Dublin" or "Vienna" or "Munich")
    {
        tags.Add("urban");
        tags.Add("student life");
    }

    return tags;
}

static HashSet<string> GetPreferenceTags(IEnumerable<string>? values)
{
    var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var value in values ?? [])
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) continue;

        var text = normalized.ToLowerInvariant();
        if (text.Contains("tech", StringComparison.OrdinalIgnoreCase) || text.Contains("computer", StringComparison.OrdinalIgnoreCase) || text.Contains("engineering", StringComparison.OrdinalIgnoreCase))
            tags.Add("tech");
        if (text.Contains("creative", StringComparison.OrdinalIgnoreCase) || text.Contains("art", StringComparison.OrdinalIgnoreCase) || text.Contains("design", StringComparison.OrdinalIgnoreCase))
            tags.Add("creative");
        if (text.Contains("grant", StringComparison.OrdinalIgnoreCase) || text.Contains("budget", StringComparison.OrdinalIgnoreCase) || text.Contains("affordable", StringComparison.OrdinalIgnoreCase))
            tags.Add("budget friendly");
        if (text.Contains("urban", StringComparison.OrdinalIgnoreCase) || text.Equals("student life", StringComparison.OrdinalIgnoreCase) || text.Contains("nightlife", StringComparison.OrdinalIgnoreCase))
            tags.Add("urban");
        if (text.Contains("social", StringComparison.OrdinalIgnoreCase) || text.Contains("friends", StringComparison.OrdinalIgnoreCase) || text.Contains("party", StringComparison.OrdinalIgnoreCase))
            tags.Add("social");
        if (text.Contains("warm", StringComparison.OrdinalIgnoreCase) || text.Contains("sun", StringComparison.OrdinalIgnoreCase) || text.Contains("beach", StringComparison.OrdinalIgnoreCase))
            tags.Add("warm");
        if (text.Contains("cold", StringComparison.OrdinalIgnoreCase) || text.Contains("winter", StringComparison.OrdinalIgnoreCase) || text.Contains("snow", StringComparison.OrdinalIgnoreCase))
            tags.Add("cold");
        if (text.Contains("safe", StringComparison.OrdinalIgnoreCase) || text.Contains("secure", StringComparison.OrdinalIgnoreCase))
            tags.Add("safe");

        if (!tags.Any())
            tags.Add(normalized.ToLowerInvariant());
    }

    return tags;
}

static ErasmusOpportunityEntity ToEntity(ErasmusOpportunity opportunity)
{
    return new ErasmusOpportunityEntity
    {
        Id = opportunity.Id,
        University = opportunity.University,
        City = opportunity.City,
        Country = opportunity.Country,
        StudyFields = opportunity.StudyFields,
        Levels = opportunity.Levels,
        Languages = opportunity.Languages,
        MinimumGpa = opportunity.MinimumGpa,
        Deadline = opportunity.Deadline,
        MonthlyGrant = opportunity.MonthlyGrant,
        SourceUrl = opportunity.SourceUrl,
        LastVerifiedAt = opportunity.LastVerifiedAt,
        VerificationStatus = opportunity.VerificationStatus
    };
}

static ErasmusOpportunity ToOpportunity(ErasmusOpportunityEntity opportunity)
{
    var cityProfile = CityProfiles.For(opportunity.City, opportunity.Country, opportunity.MonthlyGrant);
    var sourceUrl = OfficialSourceFor(opportunity.University, opportunity.SourceUrl);
    var accommodationLinks = AccommodationProfiles.For(opportunity.University, opportunity.City, opportunity.Country, sourceUrl);
    return new ErasmusOpportunity(
        opportunity.Id,
        opportunity.University,
        opportunity.City,
        opportunity.Country,
        opportunity.StudyFields,
        opportunity.Levels,
        opportunity.Languages,
        opportunity.MinimumGpa,
        opportunity.Deadline,
        opportunity.MonthlyGrant,
        sourceUrl,
        opportunity.LastVerifiedAt,
        opportunity.VerificationStatus,
        cityProfile.Rent,
        cityProfile.Living,
        cityProfile.Total,
        cityProfile.Coverage,
        ["Passport or ID copy", "Transcript of Records", "Learning Agreement or study plan", "Language certificate", "Nomination from your home university", "Proof of health insurance"],
        cityProfile.Experience,
        accommodationLinks);
}

static string OfficialSourceFor(string university, string fallback)
{
        return university switch
        {
            "Aalto University" => "https://www.aalto.fi/en",
            "Technical University of Munich" => "https://www.tum.de/en",
            "University of Amsterdam" => "https://www.uva.nl/en/education/exchange/exchange.html",
            "KTH Royal Institute of Technology" => "https://www.kth.se/en/studies/exchange",
            "University of Bologna" => "https://www.unibo.it/en/university/international-outreach/international-outreach",
            "Charles University" => "https://cuni.cz/UKEN-1.html",
            "University of Warsaw" => "https://en.uw.edu.pl/international-cooperation/",
            "Universidade de Lisboa" => "https://www.ulisboa.pt/en/international-students",
            "University of Tartu" => "https://ut.ee/en/content/exchange-studies",
            "Riga Technical University" => "https://www.rtu.lv/en",
            "Vilnius University" => "https://www.vu.lt/en",
            "Technical University of Denmark" => "https://www.dtu.dk/english/education",
            "Sorbonne University" => "https://www.sorbonne-universite.fr/en/international",
            "University of Barcelona" => "https://web.ub.edu/web/ub/estudis-internacionals",
            "University of Vienna" => "https://international.univie.ac.at/en/",
            "KU Leuven" => "https://www.kuleuven.be/english",
            "Trinity College Dublin" => "https://www.tcd.ie/study/",
            "University of Ljubljana" => "https://www.uni-lj.si/en",
            "University of Zagreb" => "https://www.unizg.hr/homepage/international-exchange/",
            "National Technical University of Athens" => "https://www.ntua.gr/en",
            "Eötvös Loránd University" => "https://www.elte.hu/en",
            "University of Oslo" => "https://www.uio.no/english/studies/exchange/",
            "ETH Zurich" => "https://ethz.ch/en/studies/international.html",
            "University of Bucharest" => "https://unibuc.ro/?lang=en",
            "Comenius University Bratislava" => "https://uniba.sk/en/international-relations/",
            "University of Porto" => "https://www.up.pt/portal/en/",
            "Humboldt University of Berlin" => "https://www.hu-berlin.de/en",
            "University of Helsinki" => "https://www.helsinki.fi/en",
            "University of Gothenburg" => "https://www.gu.se/en",
            "University of Copenhagen" => "https://studies.ku.dk/",
            "University of Lisbon" => "https://www.ulisboa.pt/en",
            "University of Milan" => "https://www.unimi.it/en",
            "University of Groningen" => "https://www.rug.nl/education/exchange/",
            "University of Innsbruck" => "https://www.uibk.ac.at/en/international/",
            "University of Leuven" => "https://www.kuleuven.be/english",
            "University of Glasgow" => "https://www.gla.ac.uk/",
            _ => fallback
        };
}

static int? GetUserId(ClaimsPrincipal principal)
{
    var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    return int.TryParse(value, out var userId) ? userId : null;
}

static string GetClientIp(HttpContext context)
{
    // Render documents the first X-Forwarded-For value as the original client IP.
    var forwardedIp = context.Request.Headers["X-Forwarded-For"]
        .ToString()
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault();

    return IPAddress.TryParse(forwardedIp, out var parsedForwardedIp)
        ? parsedForwardedIp.ToString()
        : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

static AuthResponse CreateAuthResponse(UserEntity user, string jwtKey, bool verificationRequired = false, string? verificationCode = null)
{
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Email, user.Email),
        new Claim(ClaimTypes.Name, user.DisplayName)
    };
    var credentials = new SigningCredentials(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(
        claims: claims,
        expires: DateTime.UtcNow.AddDays(7),
        signingCredentials: credentials);

    return new AuthResponse(new JwtSecurityTokenHandler().WriteToken(token), ToUserResponse(user), verificationRequired, verificationCode);
}

static string GenerateVerificationCode()
{
    const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    return string.Concat(Enumerable.Range(0, 6).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
}

static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

static bool MatchesToken(string token, string? expectedHash)
{
    if (string.IsNullOrWhiteSpace(expectedHash))
        return false;

    var actualHash = Encoding.UTF8.GetBytes(HashToken(token));
    var storedHash = Encoding.UTF8.GetBytes(expectedHash);
    return actualHash.Length == storedHash.Length && CryptographicOperations.FixedTimeEquals(actualHash, storedHash);
}

static bool TryNormalizeProfilePhoto(string dataUrl, out string? normalizedDataUrl)
{
    const string prefix = "data:image/jpeg;base64,";
    normalizedDataUrl = null;
    if (!dataUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || dataUrl.Length > 135_000)
        return false;

    try
    {
        var bytes = Convert.FromBase64String(dataUrl[prefix.Length..]);
        if (bytes.Length is < 4 or > 90_000 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF)
            return false;
        normalizedDataUrl = prefix + Convert.ToBase64String(bytes);
        return true;
    }
    catch (FormatException)
    {
        return false;
    }
}

static void EnsureAuthColumns(ErasmusDbContext database)
{
    TryAddAuthColumn(database, "ALTER TABLE Users ADD COLUMN EmailVerified INTEGER NOT NULL DEFAULT 0");
    TryAddAuthColumn(database, "ALTER TABLE Users ADD COLUMN EmailVerificationToken TEXT NULL");
    TryAddAuthColumn(database, "ALTER TABLE Users ADD COLUMN EmailVerificationTokenExpiresAt TEXT NULL");
    TryAddAuthColumn(database, "ALTER TABLE Users ADD COLUMN PasswordResetToken TEXT NULL");
    TryAddAuthColumn(database, "ALTER TABLE Users ADD COLUMN PasswordResetTokenExpiresAt TEXT NULL");
    TryAddAuthColumn(database, "ALTER TABLE Users ADD COLUMN HomeUniversity TEXT NULL");
    TryAddAuthColumn(database, "ALTER TABLE Users ADD COLUMN Intake TEXT NULL");
    TryAddAuthColumn(database, "ALTER TABLE Users ADD COLUMN Bio TEXT NULL");
    TryAddAuthColumn(database, "ALTER TABLE Users ADD COLUMN CommunityVisible INTEGER NOT NULL DEFAULT 0");
    TryAddAuthColumn(database, "ALTER TABLE Users ADD COLUMN ProfilePhotoDataUrl TEXT NULL");
    TryAddAuthColumn(database, "ALTER TABLE Users ADD COLUMN RoommateSearch INTEGER NOT NULL DEFAULT 0");
}

static void EnsureCommunityTables(ErasmusDbContext database)
{
    database.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS StudentPlans (
            Id INTEGER NOT NULL CONSTRAINT PK_StudentPlans PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NOT NULL,
            OpportunityId INTEGER NOT NULL,
            Intake TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
        )
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE UNIQUE INDEX IF NOT EXISTS IX_StudentPlans_UserId_OpportunityId_Intake
        ON StudentPlans (UserId, OpportunityId, Intake)
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS IX_StudentPlans_OpportunityId_Intake
        ON StudentPlans (OpportunityId, Intake)
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS Conversations (
            Id INTEGER NOT NULL CONSTRAINT PK_Conversations PRIMARY KEY AUTOINCREMENT,
            UserAId INTEGER NOT NULL,
            UserBId INTEGER NOT NULL,
            OpportunityId INTEGER NOT NULL,
            Intake TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            CHECK (UserAId < UserBId),
            FOREIGN KEY (UserAId) REFERENCES Users(Id) ON DELETE CASCADE,
            FOREIGN KEY (UserBId) REFERENCES Users(Id) ON DELETE CASCADE
        )
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Conversations_UserAId_UserBId_OpportunityId_Intake
        ON Conversations (UserAId, UserBId, OpportunityId, Intake)
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS Messages (
            Id INTEGER NOT NULL CONSTRAINT PK_Messages PRIMARY KEY AUTOINCREMENT,
            ConversationId INTEGER NOT NULL,
            SenderUserId INTEGER NOT NULL,
            Content TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (ConversationId) REFERENCES Conversations(Id) ON DELETE CASCADE,
            FOREIGN KEY (SenderUserId) REFERENCES Users(Id) ON DELETE CASCADE
        )
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS IX_Messages_ConversationId_Id
        ON Messages (ConversationId, Id)
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS DeadlineReminders (
            Id INTEGER NOT NULL CONSTRAINT PK_DeadlineReminders PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NOT NULL,
            OpportunityId INTEGER NOT NULL,
            DaysBefore INTEGER NOT NULL DEFAULT 7,
            CreatedAt TEXT NOT NULL,
            SentAt TEXT NULL,
            FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
        )
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE UNIQUE INDEX IF NOT EXISTS IX_DeadlineReminders_UserId_OpportunityId
        ON DeadlineReminders (UserId, OpportunityId)
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS CommunityBlocks (
            Id INTEGER NOT NULL CONSTRAINT PK_CommunityBlocks PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NOT NULL,
            BlockedUserId INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL,
            CHECK (UserId <> BlockedUserId),
            FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE,
            FOREIGN KEY (BlockedUserId) REFERENCES Users(Id) ON DELETE CASCADE
        )
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE UNIQUE INDEX IF NOT EXISTS IX_CommunityBlocks_UserId_BlockedUserId
        ON CommunityBlocks (UserId, BlockedUserId)
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS CommunityReports (
            Id INTEGER NOT NULL CONSTRAINT PK_CommunityReports PRIMARY KEY AUTOINCREMENT,
            ReporterUserId INTEGER NOT NULL,
            ReportedUserId INTEGER NOT NULL,
            ConversationId INTEGER NULL,
            Reason TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (ReporterUserId) REFERENCES Users(Id) ON DELETE CASCADE,
            FOREIGN KEY (ReportedUserId) REFERENCES Users(Id) ON DELETE CASCADE,
            FOREIGN KEY (ConversationId) REFERENCES Conversations(Id) ON DELETE SET NULL
        )
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS ConversationReadStates (
            Id INTEGER NOT NULL CONSTRAINT PK_ConversationReadStates PRIMARY KEY AUTOINCREMENT,
            ConversationId INTEGER NOT NULL,
            UserId INTEGER NOT NULL,
            LastReadMessageId INTEGER NOT NULL DEFAULT 0,
            UpdatedAt TEXT NOT NULL,
            FOREIGN KEY (ConversationId) REFERENCES Conversations(Id) ON DELETE CASCADE,
            FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
        )
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE UNIQUE INDEX IF NOT EXISTS IX_ConversationReadStates_ConversationId_UserId
        ON ConversationReadStates (ConversationId, UserId)
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS StudentExperiences (
            Id INTEGER NOT NULL CONSTRAINT PK_StudentExperiences PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NOT NULL,
            OpportunityId INTEGER NOT NULL,
            Rating INTEGER NOT NULL,
            ExperienceType TEXT NOT NULL,
            Intake TEXT NOT NULL,
            Content TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
        )
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE UNIQUE INDEX IF NOT EXISTS IX_StudentExperiences_UserId_OpportunityId
        ON StudentExperiences (UserId, OpportunityId)
        """);
}

static void EnsureFeatureTables(ErasmusDbContext database)
{
    TryAddAuthColumn(database, "ALTER TABLE StudentPlans ADD COLUMN Stage TEXT NOT NULL DEFAULT 'Considering'");
    TryAddAuthColumn(database, "ALTER TABLE StudentPlans ADD COLUMN ChecklistJson TEXT NOT NULL DEFAULT '[]'");
    TryAddAuthColumn(database, "ALTER TABLE StudentPlans ADD COLUMN UpdatedAt TEXT NOT NULL DEFAULT '2000-01-01T00:00:00+00:00'");
    database.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS CatalogCorrections (
            Id INTEGER NOT NULL CONSTRAINT PK_CatalogCorrections PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NOT NULL,
            OpportunityId INTEGER NOT NULL,
            FieldsJson TEXT NOT NULL,
            SourceUrl TEXT NULL,
            Note TEXT NOT NULL,
            Status TEXT NOT NULL DEFAULT 'Pending',
            ReviewerNote TEXT NULL,
            CreatedAt TEXT NOT NULL,
            ReviewedAt TEXT NULL,
            FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
        )
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS IX_CatalogCorrections_Status_CreatedAt
        ON CatalogCorrections (Status, CreatedAt)
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS OpportunityQuestions (
            Id INTEGER NOT NULL CONSTRAINT PK_OpportunityQuestions PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NOT NULL,
            OpportunityId INTEGER NOT NULL,
            Content TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
        )
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS IX_OpportunityQuestions_OpportunityId_CreatedAt
        ON OpportunityQuestions (OpportunityId, CreatedAt)
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS OpportunityAnswers (
            Id INTEGER NOT NULL CONSTRAINT PK_OpportunityAnswers PRIMARY KEY AUTOINCREMENT,
            QuestionId INTEGER NOT NULL,
            UserId INTEGER NOT NULL,
            Content TEXT NOT NULL,
            ExperienceType TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (QuestionId) REFERENCES OpportunityQuestions(Id) ON DELETE CASCADE,
            FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
        )
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE UNIQUE INDEX IF NOT EXISTS IX_OpportunityAnswers_QuestionId_UserId
        ON OpportunityAnswers (QuestionId, UserId)
        """);
}

static void EnsurePushTables(ErasmusDbContext database)
{
    database.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS PushDevices (
            Id INTEGER NOT NULL CONSTRAINT PK_PushDevices PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NOT NULL,
            ExpoPushToken TEXT NOT NULL,
            Platform TEXT NOT NULL DEFAULT 'unknown',
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
        )
        """);
    database.Database.ExecuteSqlRaw("""
        CREATE UNIQUE INDEX IF NOT EXISTS IX_PushDevices_UserId_ExpoPushToken
        ON PushDevices (UserId, ExpoPushToken)
        """);
}

static string[] DeserializeStringArray(string json)
{
    try
    {
        return System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? [];
    }
    catch (System.Text.Json.JsonException)
    {
        return [];
    }
}

static string[] ParseCatalogList(string value) => value
    .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .Take(20)
    .ToArray();

static bool HasCatalogReviewAccess(HttpRequest request, IConfiguration configuration)
{
    var configuredKey = configuration["CatalogReview:Key"];
    var providedKey = request.Headers["X-Catalog-Review-Key"].ToString();
    if (string.IsNullOrWhiteSpace(configuredKey) || string.IsNullOrWhiteSpace(providedKey))
        return false;
    var expected = Encoding.UTF8.GetBytes(configuredKey);
    var supplied = Encoding.UTF8.GetBytes(providedKey);
    return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
}

static void TryAddAuthColumn(ErasmusDbContext database, string sql)
{
    try
    {
        database.Database.ExecuteSqlRaw(sql);
    }
    catch (DbException exception) when (
        exception.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
    {
        // The column already exists in a database upgraded by an earlier startup.
    }
}

static Task<bool> SendVerificationCodeAsync(string email, string code, IConfiguration configuration, ILogger logger, bool allowDevelopmentFallback)
{
    return SendEmailAsync(
        email,
        "Your Erasmus With Us verification code",
        $"Your six-letter Erasmus With Us verification code is: {code}\n\nIt expires in 15 minutes.",
        configuration,
        logger,
        allowDevelopmentFallback);
}

static async Task<bool> SendEmailAsync(string email, string subject, string body, IConfiguration configuration, ILogger logger, bool allowDevelopmentFallback)
{
    var host = configuration["Email:SmtpHost"];
    var from = configuration["Email:From"] ?? configuration["Email:Username"];
    if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from))
    {
        if (allowDevelopmentFallback)
            logger.LogWarning("SMTP is not configured. Development email for {Email}: {Body}", email, body);
        else
            logger.LogError("SMTP host and sender must be configured to send account emails.");
        return allowDevelopmentFallback;
    }

    try
    {
        var senderAddress = new MailAddress(from).Address;
        var senderName = configuration["Email:FromName"]?.Trim() is { Length: > 0 } displayName
            ? displayName
            : "Erasmus With Us";
        var sender = new MailAddress(senderAddress, senderName, Encoding.UTF8);
        using var client = new SmtpClient(host, configuration.GetValue<int?>("Email:SmtpPort") ?? 587)
        {
            EnableSsl = configuration.GetValue("Email:EnableSsl", true),
            Credentials = new NetworkCredential(configuration["Email:Username"], configuration["Email:Password"])
        };
        using var message = new MailMessage(sender, new MailAddress(email))
        {
            Subject = subject,
            Body = body
        };
        await client.SendMailAsync(message);
        return true;
    }
    catch (Exception exception) when (exception is SmtpException or ArgumentException or FormatException or InvalidOperationException)
    {
        logger.LogError(exception, "Could not send account email to {Email}", email);
        return false;
    }
}

static UserResponse ToUserResponse(UserEntity user)
{
    return new UserResponse(
        user.Id,
        user.Email,
        user.DisplayName,
        user.Country,
        user.StudyField,
        user.Level,
        user.Gpa,
        System.Text.Json.JsonSerializer.Deserialize<string[]>(user.PreferredCountriesJson) ?? [],
        System.Text.Json.JsonSerializer.Deserialize<string[]>(user.LanguagesJson) ?? [],
        user.EmailVerified,
        user.CreatedAt,
        user.HomeUniversity,
        user.Intake,
        user.Bio,
        user.CommunityVisible,
        user.ProfilePhotoDataUrl,
        user.RoommateSearch);
}

static DeadlineReminderResponse ToDeadlineReminderResponse(DeadlineReminderEntity reminder, ErasmusOpportunity[] catalog)
{
    var opportunity = catalog.First(item => item.Id == reminder.OpportunityId);
    return new DeadlineReminderResponse(reminder.Id, reminder.OpportunityId, opportunity.University,
        opportunity.Deadline, reminder.DaysBefore, reminder.CreatedAt, reminder.SentAt);
}

record RegisterRequest(string Email, string Password, string DisplayName);
record LoginRequest(string Email, string Password);
record ForgotPasswordRequest(string Email);
record ResetPasswordRequest(string Email, string Token, string NewPassword);
record EmailVerificationRequest(string Email);
record VerifyEmailRequest(string Email, string Token);
record UpdateProfileRequest(string? DisplayName, string? Country, string? StudyField, string? Level, double? Gpa,
    string[] PreferredCountries, string[] Languages, string? HomeUniversity = null, string? Intake = null,
    string? Bio = null, bool? CommunityVisible = null, string? ProfilePhotoDataUrl = null,
    bool RemoveProfilePhoto = false, bool? RoommateSearch = null);
record UserResponse(int Id, string Email, string DisplayName, string? Country, string? StudyField,
    string? Level, double? Gpa, string[] PreferredCountries, string[] Languages, bool EmailVerified,
    DateTimeOffset CreatedAt, string? HomeUniversity, string? Intake, string? Bio, bool CommunityVisible,
    string? ProfilePhotoDataUrl, bool RoommateSearch);
record AuthResponse(string Token, UserResponse User, bool VerificationRequired = false, string? VerificationCode = null);
record CreateStudentPlanRequest(int OpportunityId, string? Intake);
record UpdateStudentPlanRequest(string? Stage, string[]? CompletedTasks);
record SaveDeadlineReminderRequest(int OpportunityId, int DaysBefore = 7);
record DeadlineReminderResponse(int Id, int OpportunityId, string University, string Deadline, int DaysBefore,
    DateTimeOffset CreatedAt, DateTimeOffset? SentAt);
record ReportCommunityUserRequest(int ReportedUserId, int? ConversationId, string? Reason);
record SaveStudentExperienceRequest(int Rating, string? ExperienceType, string? Intake, string? Content);
record StudentExperienceResponse(int Id, string DisplayName, int Rating, string ExperienceType, string Intake,
    string Content, DateTimeOffset CreatedAt);
record ExperienceSummaryResponse(double AverageRating, int Count, StudentExperienceResponse[] Experiences);
record StudentPlanResponse(int Id, int OpportunityId, string University, string Intake, DateTimeOffset CreatedAt,
    string Stage, string[] CompletedTasks, DateTimeOffset UpdatedAt);
record StudentCardResponse(int Id, string DisplayName, string? HomeUniversity, string? StudyField,
    string? Level, string? Intake, string? Bio, string? ProfilePhotoDataUrl, bool RoommateSearch);
record SubmitCatalogCorrectionRequest(Dictionary<string, string?>? Fields, string? SourceUrl, string? Note);
record ReviewCatalogCorrectionRequest(string? Status, string? ReviewerNote);
record CatalogCorrectionResponse(int Id, int OpportunityId, string University, string DisplayName,
    Dictionary<string, string?> Fields, string? SourceUrl, string Note, string Status,
    string? ReviewerNote, DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt);
record SaveOpportunityQuestionRequest(string? Content);
record SaveOpportunityAnswerRequest(string? Content, string? ExperienceType);
record OpportunityAnswerResponse(int Id, string DisplayName, string ExperienceType, string Content, DateTimeOffset CreatedAt);
record OpportunityQuestionResponse(int Id, string DisplayName, string Content, DateTimeOffset CreatedAt,
    OpportunityAnswerResponse[] Answers);
record StartConversationRequest(int RecipientUserId, int OpportunityId, string? Intake);
record SendMessageRequest(string? Content);
record MessageResponse(int Id, int SenderUserId, string Content, DateTimeOffset CreatedAt);
record MessagePreview(string Content, DateTimeOffset CreatedAt);
record ConversationSummaryResponse(int Id, int OtherUserId, string OtherDisplayName, string? OtherHomeUniversity,
    string? OtherProfilePhotoDataUrl, string University, string Intake, string? LastMessage,
    DateTimeOffset? LastMessageAt, DateTimeOffset CreatedAt, int UnreadCount = 0, bool IsBlocked = false);
record RegisterPushDeviceRequest(string? ExpoPushToken, string? Platform);
record AssistantRequest(string? Message);
record AiCompletionResponse(AiChoice[]? Choices);
record AiChoice(AiMessage? Message);
record AiMessage(string? Content);

record StudentProfile(
    string? HomeUniversity,
    string? Intake,
    string? Country,
    string? StudyField,
    string? Level,
    double Gpa,
    string[] PreferredCountries,
    string[] Languages,
    string[]? Likes,
    string[]? Dislikes,
    double? Budget);

record MatchResult(ErasmusOpportunity Opportunity, int Score, string[] Reasons, MatchFactor[] Breakdown,
    EligibilityAssessment Eligibility);
record MatchFactor(string Name, int Points, int MaxPoints);
record EligibilityAssessment(bool MeetsPublishedRequirements, bool HasEnoughProfileData, bool AgreementVerified,
    string[] Reasons);

record ErasmusOpportunity(
    int Id,
    string University,
    string City,
    string Country,
    string[] StudyFields,
    string[] Levels,
    string[] Languages,
    double MinimumGpa,
    string Deadline,
    int MonthlyGrant,
    string SourceUrl,
    string LastVerifiedAt,
    string VerificationStatus,
    int MonthlyRentEstimate = 0,
    int MonthlyLivingEstimate = 0,
    int StudentBudgetEstimate = 0,
    int GrantCoveragePercent = 0,
    string[]? ApplicationDocuments = null,
    string StudentExperience = "Student feedback varies by cohort; check the latest exchange reports.",
    AccommodationLink[]? AccommodationLinks = null);

record AccommodationLink(string Name, string Url, string Note);

record CityProfile(int Rent, int Living, int Total, int Coverage, string Experience);

static class CityProfiles
{
    public static CityProfile For(string city, string country, int grant)
    {
        var profile = city switch
        {
            "Berlin" => (550, 430, "Large international student community, strong public transport, and good part-time opportunities."),
            "Munich" => (700, 500, "Excellent technical ecosystem and student support, but housing competition is high."),
            "Amsterdam" => (800, 520, "Very international and bike-friendly, with a serious housing shortage before arrival."),
            "Stockholm" => (650, 510, "High-quality student life and services; winter and accommodation deposits need planning."),
            "Helsinki" or "Espoo" => (520, 470, "Reliable student services, safe daily life, and good English coverage; winter costs are real."),
            "Paris" => (750, 520, "Huge academic network and culture, but living outside the centre is usually necessary."),
            "Dublin" => (850, 500, "Friendly and English-speaking with strong employers, though rent is the main pressure."),
            "Copenhagen" or "Lyngby" => (700, 520, "Excellent cycling and student infrastructure; food and rent are above the European average."),
            "Lisbon" or "Porto" => (450, 380, "Social, sunny, and popular with exchange students; central rooms fill up quickly."),
            "Barcelona" => (550, 420, "Very social and international, with a competitive room market near the main universities."),
            "Milan" or "Bologna" => (520, 400, "Strong student culture and transport; shared rooms outside the centre give the best value."),
            "Vienna" or "Innsbruck" => (500, 420, "Safe, organised, and student-friendly; university housing applications should be early."),
            "Leuven" => (500, 390, "Compact student town with excellent cycling and a strong university community."),
            "Warsaw" or "Prague" => (350, 320, "Good value for a capital city, with active student communities and manageable transport costs."),
            "Tartu" or "Riga" or "Vilnius" => (300, 300, "Affordable and easy to navigate, with a smaller but active international student scene."),
            "Athens" or "Zagreb" or "Ljubljana" => (360, 330, "Good social atmosphere and moderate costs; check course language carefully."),
            "Oslo" => (750, 560, "Very safe and outdoors-oriented, but food and housing can quickly exceed the grant."),
            "Zurich" => (900, 650, "Outstanding academic environment and salaries, but the grant alone will not cover a typical budget."),
            "Glasgow" => (600, 450, "Welcoming student city with strong societies and a broad course choice; budget for currency changes."),
            _ => (420, 380, "Exchange students usually report a supportive campus and manageable costs with early housing planning.")
        };

        var total = profile.Item1 + profile.Item2;
        var coverage = total == 0 ? 0 : Math.Clamp((int)Math.Round(grant * 100d / total), 0, 100);
        return new CityProfile(profile.Item1, profile.Item2, total, coverage, profile.Item3);
    }
}

static class AccommodationProfiles
{
    public static AccommodationLink[] For(string university, string city, string country, string officialSourceUrl)
    {
        return university switch
        {
            "Aalto University" => Links(("HOAS student housing", "https://hoas.fi/en/", "Student rooms and shared apartments around Helsinki and Espoo."), ("Aalto housing guide", "https://www.aalto.fi/en/student-life/housing", "University guidance for exchange-student housing."), ("Oikotie rentals", "https://asunnot.oikotie.fi/vuokra-asunnot", "Finnish rental listings; filter for Espoo and shared homes.")),
            "Technical University of Munich" => Links(("Studierendenwerk München", "https://www.stwm.de/en/accommodation/", "Official student residence information for Munich."), ("TUM housing", "https://www.tum.de/en/students/organisational-details/housing", "TUM guidance and housing search advice."), ("WG-Gesucht Munich", "https://www.wg-gesucht.de/en/wg-zimmer-in-Muenchen.90.0.1.0.html", "Popular shared-flat listings for students.")),
            "University of Amsterdam" => Links(("UvA housing", "https://www.uva.nl/en/education/exchange/housing/housing.html", "University housing information for exchange students."), ("ROOM.nl", "https://www.room.nl/en/", "Student housing registration and rooms in the Netherlands."), ("Kamernet Amsterdam", "https://kamernet.nl/en/for-rent/room-amsterdam", "Private rooms and shared apartments in Amsterdam.")),
            "KTH Royal Institute of Technology" => Links(("SSSB Stockholm", "https://www.sssb.se/en/", "Main student housing queue in Stockholm."), ("KTH accommodation", "https://www.kth.se/en/student/studies/accommodation", "KTH exchange-student housing guidance."), ("Qasa Stockholm", "https://qasa.se/en", "Private rooms and apartments with digital contracts.")),
            "University of Bologna" => Links(("ER.GO residences", "https://www.er-go.it/index.php?id=675", "Regional student residences and accommodation calls."), ("Unibo housing", "https://www.unibo.it/en/international/incoming-exchange-students/accommodation", "University housing guidance for incoming exchange students."), ("HousingAnywhere Bologna", "https://housinganywhere.com/s/Bologna--Italy", "Furnished rooms and shared flats near campus.")),
            "Charles University" => Links(("Charles University dorms", "https://cuni.cz/UKEN-125.html", "Official dormitory information for international students."), ("Prague Student Living", "https://www.studentliving.cz/en/", "Private student residences in Prague."), ("Bezrealitky Prague", "https://www.bezrealitky.com/", "Local Czech rental listings; check contracts carefully.")),
            "University of Warsaw" => Links(("University of Warsaw dorms", "https://welcome.uw.edu.pl/before-you-arrive/accommodation/", "University residence and arrival information."), ("Pepe Housing Warsaw", "https://www.pepehousing.com/", "Student rooms and apartments in Warsaw."), ("OLX Warsaw rentals", "https://www.olx.pl/nieruchomosci/stancje-pokoje/warszawa/", "Local room listings; use verified landlords.")),
            "Universidade de Lisboa" => Links(("SASUL residences", "https://www.sas.universidade.pt/", "University social-action housing information."), ("ULisboa accommodation", "https://www.ulisboa.pt/en/info/accommodation", "University housing guidance for international students."), ("Uniplaces Lisbon", "https://www.uniplaces.com/accommodation/lisbon", "Furnished student rooms with online booking.")),
            "University of Tartu" => Links(("Tartu Student Village", "https://campus.ee/en/", "Main student residence provider in Tartu."), ("University of Tartu housing", "https://ut.ee/en/content/accommodation", "Official exchange-student accommodation guidance."), ("KV.ee Tartu", "https://www.kv.ee/en/", "Local Estonian rental listings.")),
            "Riga Technical University" => Links(("RTU dormitories", "https://www.rtu.lv/en/students/hostels", "Official RTU hostel information."), ("Riga student housing", "https://www.studenthotel.lv/", "Student residence rooms in Riga."), ("City24 Riga", "https://www.city24.lv/en/real-estate/rent/", "Local rental listings and shared flats.")),
            "Vilnius University" => Links(("VU dormitories", "https://www.vu.lt/en/studies/accommodation", "Vilnius University residence information."), ("VGTU student housing", "https://apgyvendinimas.vilniustech.lt/", "Local student residence alternative."), ("Aruodas Vilnius", "https://m.en.aruodas.lt/", "Lithuanian rental listings.")),
            "Technical University of Denmark" => Links(("DTU accommodation", "https://www.dtu.dk/english/education/student-life/accommodation", "DTU housing guidance and residence options."), ("KKIK student housing", "https://www.kkik.dk/", "Student rooms around Copenhagen."), ("Housing Foundation Copenhagen", "https://housingfoundation.dk/", "Furnished student housing for international students.")),
            "Sorbonne University" => Links(("CROUS Paris", "https://www.crous-paris.fr/logements/", "Official public student residences in Paris."), ("Sorbonne housing", "https://sciences.sorbonne-universite.fr/en/international", "International student arrival and housing guidance."), ("Studapart Paris", "https://www.studapart.com/en/accommodation-paris", "Student rooms and residences with online applications.")),
            "University of Barcelona" => Links(("BCU student residences", "https://www.bcu.cat/en/", "Barcelona university community accommodation network."), ("UB accommodation", "https://www.ub.edu/web/ub/en/estudis/internacionals/estudiants_internacionals/allotjament/index.html", "University guidance for international students."), ("Badi Barcelona", "https://badi.com/", "Shared rooms and co-living listings.")),
            "University of Vienna" => Links(("OeAD student housing", "https://oeadstudenthousing.at/en/", "Main residence provider for international students in Vienna."), ("University of Vienna housing", "https://studieren.univie.ac.at/en/accommodation/", "Official housing and arrival information."), ("STUWO Vienna", "https://www.stuwo.at/en/student-accommodation/vienna/", "Private student residences.")),
            "KU Leuven" => Links(("KU Leuven Kotwijs", "https://kotwijs.be/en", "Official Leuven student room platform."), ("KU Leuven housing", "https://www.kuleuven.be/english/study/life-at-ku-leuven/housing", "University housing advice and residence links."), ("Cohabs Leuven", "https://cohabs.com/", "Co-living rooms and shared facilities.")),
            "Trinity College Dublin" => Links(("Trinity accommodation", "https://www.tcd.ie/accommodation/", "Official Trinity residence information."), ("DCU campus residences", "https://www.dcuaccommodation.ie/", "Student residence alternative in Dublin."), ("Daft Dublin rooms", "https://www.daft.ie/sharing/dublin/rooms-to-rent", "Irish room and house-share listings.")),
            "University of Ljubljana" => Links(("University of Ljubljana dorms", "https://www.uni-lj.si/study/international-students/accommodation", "Official international student accommodation guidance."), ("Student Housing Ljubljana", "https://www.stud-dom-lj.si/en/", "Public student residence information."), ("Nepremicnine Ljubljana", "https://www.nepremicnine.net/en/", "Local Slovenian rental listings.")),
            "University of Zagreb" => Links(("Student Centre Zagreb", "https://www.sczg.unizg.hr/smjestaj", "Official student dormitories in Zagreb."), ("University of Zagreb housing", "https://www.unizg.hr/homepage/international-exchange-students/", "Exchange arrival and accommodation guidance."), ("Njuškalo Zagreb", "https://www.njuskalo.hr/iznajmljivanje-soba", "Local room listings.")),
            "National Technical University of Athens" => Links(("NTUA international", "https://www.ntua.gr/en/international", "University contact and accommodation guidance."), ("ESN Athens housing", "https://esnathens.gr/", "Exchange-student community and housing support."), ("Spitogatos Athens", "https://en.spitogatos.gr/", "Greek rental listings.")),
            "Eötvös Loránd University" => Links(("ELTE dormitories", "https://www.elte.hu/en/dormitories", "Official residence information for exchange students."), ("Student Hotel Budapest", "https://www.thestudenthotel.com/budapest/", "Private student residence and co-living."), ("Alberlet Budapest", "https://ingatlan.com/en", "Hungarian rental listings.")),
            "University of Oslo" => Links(("SiO housing", "https://www.sio.no/en/housing", "Main student housing provider in Oslo."), ("University of Oslo housing", "https://www.uio.no/english/studies/international-students/housing/", "Official exchange-student housing information."), ("Hybel Oslo", "https://hybel.no/", "Norwegian room and flat listings.")),
            "ETH Zurich" => Links(("WOKO Zurich", "https://www.woko.ch/en/", "Main student residence association in Zurich."), ("ETH housing", "https://ethz.ch/students/en/studies/arrival/housing.html", "Official ETH housing guidance."), ("Flatfox Zurich", "https://flatfox.ch/en/", "Swiss rental listings and applications.")),
            "University of Porto" => Links(("SASUP residences", "https://www2.sas.up.pt/", "University social-action accommodation."), ("University of Porto housing", "https://www.up.pt/portal/en/exchange-students/", "Official exchange-student arrival guidance."), ("Uniplaces Porto", "https://www.uniplaces.com/accommodation/porto", "Furnished student rooms and apartments.")),
            "Humboldt University of Berlin" => Links(("Studierendenwerk Berlin", "https://www.stw.berlin/en/housing.html", "Official student residences in Berlin."), ("HU Berlin housing", "https://www.hu-berlin.de/en/students/counselling/housing", "University housing advice."), ("WG-Gesucht Berlin", "https://www.wg-gesucht.de/en/wg-zimmer-in-Berlin.8.0.1.0.html", "Largest shared-flat search for Berlin.")),
            "University of Helsinki" => Links(("HOAS Helsinki", "https://hoas.fi/en/", "Student rooms and apartments in Helsinki."), ("Uniarts student housing", "https://www.helsinki.fi/en/studying/student-life/housing", "Helsinki university housing guidance."), ("Vuokraovi Helsinki", "https://www.vuokraovi.com/", "Finnish rental listings.")),
            "University of Gothenburg" => Links(("SGS Studentbostäder", "https://www.sgs.se/en", "Main student residence provider in Gothenburg."), ("University of Gothenburg housing", "https://www.gu.se/en/study-in-gothenburg/accommodation", "Official exchange housing information."), ("Boplats Gothenburg", "https://nya.boplats.se/en", "Local housing queue and rental listings.")),
            "University of Copenhagen" => Links(("Housing Foundation", "https://housingfoundation.dk/", "Furnished residences for Copenhagen international students."), ("UCPH housing", "https://studies.ku.dk/exchange/accommodation/", "Official University of Copenhagen housing guidance."), ("Findroommate Copenhagen", "https://www.findroommate.dk/en", "Shared-room and co-living search.")),
            "University of Glasgow" => Links(("Glasgow accommodation", "https://www.gla.ac.uk/myglasgow/accommodation/", "Official university residences and application guidance."), ("CRM Students Glasgow", "https://www.crm-students.com/student-accommodation/glasgow/", "Private student residences near campus."), ("SpareRoom Glasgow", "https://www.spareroom.co.uk/flatshare/glasgow", "Room and flat-share listings.")),
            _ => Links(($"{university} official source", officialSourceUrl, "University-owned exchange page; check its accommodation or international-student section."), ($"HousingAnywhere {city}", $"https://housinganywhere.com/s/{Uri.EscapeDataString(city + ", " + country)}", $"Rooms and studios near {university}; compare dates and booking terms."), ($"Erasmusu {city}", $"https://erasmusu.com/en/erasmus-{city.ToLowerInvariant().Replace(' ', '-')}/housing", $"Exchange-student rooms near {university}; check reviews and contracts."))
        };
    }

    private static AccommodationLink[] Links(params (string Name, string Url, string Note)[] links)
    {
        return links.Select(link => new AccommodationLink(link.Name, link.Url, link.Note)).ToArray();
    }

    private static string FindUniversityUrl(string university)
    {
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(university + " student accommodation official");
    }
}

static class SeedData
{
    public static string GenerateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    public static readonly ErasmusOpportunity[] Opportunities =
    [
        new(1, "Aalto University", "Espoo", "Finland", ["Computer Science", "Design", "Business"], ["Bachelor", "Master"], ["English"], 3.0, "2027-02-15", 540, "https://www.aalto.fi/en/study-options/erasmus-exchange", "2026-10-08", "Verified"),
        new(2, "Technical University of Munich", "Munich", "Germany", ["Computer Science", "Engineering", "Business"], ["Bachelor", "Master"], ["English", "German"], 3.2, "2027-01-31", 490, "https://www.international.tum.de/en/going-abroad/erasmus/", "2026-10-08", "Verified"),
        new(3, "University of Amsterdam", "Amsterdam", "Netherlands", ["Computer Science", "Economics", "Social Sciences"], ["Bachelor", "Master"], ["English", "Dutch"], 3.0, "2027-02-01", 540, "https://www.uva.nl/en/education/exchange.html", "2026-10-08", "Verified"),
        new(4, "KTH Royal Institute of Technology", "Stockholm", "Sweden", ["Computer Science", "Engineering", "Architecture"], ["Master"], ["English", "Swedish"], 3.0, "2027-02-10", 540, "https://www.kth.se/en/studies/exchange", "2026-10-08", "Verified"),
        new(5, "University of Bologna", "Bologna", "Italy", ["Business", "Law", "Engineering", "Arts"], ["Bachelor", "Master"], ["English", "Italian"], 2.7, "2027-03-01", 490, "https://www.unibo.it/en/international", "2026-10-08", "NeedsReview"),
        new(6, "Charles University", "Prague", "Czechia", ["Medicine", "Computer Science", "Social Sciences"], ["Bachelor", "Master"], ["English", "Czech"], 2.8, "2027-02-20", 490, "https://cuni.cz/UKEN-1.html", "2026-10-08", "NeedsReview"),
        new(7, "University of Warsaw", "Warsaw", "Poland", ["Computer Science", "Economics", "Political Science"], ["Bachelor", "Master"], ["English", "Polish"], 2.5, "2027-03-15", 490, "https://en.uw.edu.pl/international-cooperation/", "2026-10-08", "NeedsReview"),
        new(8, "Universidade de Lisboa", "Lisbon", "Portugal", ["Business", "Engineering", "Architecture"], ["Bachelor", "Master"], ["English", "Portuguese"], 2.5, "2027-03-10", 490, "https://www.ulisboa.pt/en/international", "2026-10-08", "NeedsReview"),
        new(9, "University of Tartu", "Tartu", "Estonia", ["Computer Science", "Business", "Social Sciences"], ["Bachelor", "Master"], ["English", "Estonian"], 2.5, "2027-02-28", 540, "https://ut.ee/en/content/exchange-studies", "2026-10-08", "NeedsReview"),
        new(10, "Riga Technical University", "Riga", "Latvia", ["Computer Science", "Engineering", "Architecture"], ["Bachelor", "Master"], ["English", "Latvian"], 2.5, "2027-03-01", 540, "https://www.rtu.lv/en/international-cooperation", "2026-10-08", "NeedsReview"),
        new(11, "Vilnius University", "Vilnius", "Lithuania", ["Computer Science", "Business", "Life Sciences"], ["Bachelor", "Master"], ["English", "Lithuanian"], 2.5, "2027-03-05", 540, "https://www.vu.lt/en/international-relations", "2026-10-08", "NeedsReview"),
        new(12, "Technical University of Denmark", "Lyngby", "Denmark", ["Engineering", "Computer Science", "Design"], ["Bachelor", "Master"], ["English", "Danish"], 3.0, "2027-02-01", 540, "https://www.dtu.dk/english/education/exchange", "2026-10-08", "NeedsReview"),
        new(13, "Sorbonne University", "Paris", "France", ["Science", "Engineering", "Arts"], ["Bachelor", "Master"], ["English", "French"], 2.8, "2027-02-15", 490, "https://www.sorbonne-universite.fr/en/international", "2026-10-08", "NeedsReview"),
        new(14, "University of Barcelona", "Barcelona", "Spain", ["Business", "Computer Science", "Social Sciences"], ["Bachelor", "Master"], ["English", "Spanish", "Catalan"], 2.5, "2027-03-15", 490, "https://www.ub.edu/web/ub/en/estudis/internacionals", "2026-10-08", "NeedsReview"),
        new(15, "University of Vienna", "Vienna", "Austria", ["Business", "Law", "Social Sciences"], ["Bachelor", "Master"], ["English", "German"], 2.7, "2027-02-28", 490, "https://international.univie.ac.at/en/", "2026-10-08", "NeedsReview"),
        new(16, "KU Leuven", "Leuven", "Belgium", ["Engineering", "Business", "Social Sciences"], ["Bachelor", "Master"], ["English", "Dutch", "French"], 3.0, "2027-01-31", 540, "https://www.kuleuven.be/english/education/student/exchange", "2026-10-08", "NeedsReview"),
        new(17, "Trinity College Dublin", "Dublin", "Ireland", ["Computer Science", "Business", "Arts"], ["Bachelor", "Master"], ["English"], 3.0, "2027-02-10", 540, "https://www.tcd.ie/study/abroad/", "2026-10-08", "NeedsReview"),
        new(18, "University of Ljubljana", "Ljubljana", "Slovenia", ["Computer Science", "Business", "Engineering"], ["Bachelor", "Master"], ["English", "Slovenian"], 2.5, "2027-03-01", 490, "https://www.uni-lj.si/study/international-students", "2026-10-08", "NeedsReview"),
        new(19, "University of Zagreb", "Zagreb", "Croatia", ["Engineering", "Economics", "Social Sciences"], ["Bachelor", "Master"], ["English", "Croatian"], 2.5, "2027-03-15", 490, "https://www.unizg.hr/homepage/international-exchange/", "2026-10-08", "NeedsReview"),
        new(20, "National Technical University of Athens", "Athens", "Greece", ["Engineering", "Architecture", "Computer Science"], ["Bachelor", "Master"], ["English", "Greek"], 2.5, "2027-03-10", 490, "https://www.ntua.gr/en/international", "2026-10-08", "NeedsReview"),
        new(21, "Eötvös Loránd University", "Budapest", "Hungary", ["Computer Science", "Business", "Social Sciences"], ["Bachelor", "Master"], ["English", "Hungarian"], 2.5, "2027-03-01", 490, "https://www.elte.hu/en/international", "2026-10-08", "NeedsReview"),
        new(22, "University of Oslo", "Oslo", "Norway", ["Computer Science", "Social Sciences", "Law"], ["Bachelor", "Master"], ["English", "Norwegian"], 2.8, "2027-02-15", 540, "https://www.uio.no/english/studies/exchange/", "2026-10-08", "NeedsReview"),
        new(23, "ETH Zurich", "Zurich", "Switzerland", ["Computer Science", "Engineering", "Architecture"], ["Bachelor", "Master"], ["English", "German"], 3.4, "2027-01-15", 540, "https://ethz.ch/en/studies/international.html", "2026-10-08", "NeedsReview"),
        new(24, "University of Bucharest", "Bucharest", "Romania", ["Computer Science", "Business", "Political Science"], ["Bachelor", "Master"], ["English", "Romanian"], 2.5, "2027-03-20", 490, "https://unibuc.ro/e-n/international-relations/", "2026-10-08", "NeedsReview"),
        new(25, "Comenius University Bratislava", "Bratislava", "Slovakia", ["Medicine", "Computer Science", "Economics"], ["Bachelor", "Master"], ["English", "Slovak"], 2.5, "2027-03-15", 490, "https://uniba.sk/en/international-relations/", "2026-10-08", "NeedsReview"),
        new(26, "University of Porto", "Porto", "Portugal", ["Engineering", "Medicine", "Business"], ["Bachelor", "Master"], ["English", "Portuguese"], 2.5, "2027-03-10", 490, "https://www.up.pt/portal/en/exchange-students/", "2026-10-08", "NeedsReview"),
        new(27, "Humboldt University of Berlin", "Berlin", "Germany", ["Arts", "Social Sciences", "Computer Science"], ["Bachelor", "Master"], ["English", "German"], 2.8, "2027-02-15", 490, "https://www.hu-berlin.de/en/international", "2026-10-08", "NeedsReview"),
        new(28, "University of Helsinki", "Helsinki", "Finland", ["Computer Science", "Life Sciences", "Social Sciences"], ["Bachelor", "Master"], ["English", "Finnish"], 3.0, "2027-02-15", 540, "https://www.helsinki.fi/en/admissions-and-education/studying-helsinki/exchange-studies", "2026-10-08", "NeedsReview"),
        new(29, "University of Gothenburg", "Gothenburg", "Sweden", ["Business", "Social Sciences", "Computer Science"], ["Bachelor", "Master"], ["English", "Swedish"], 2.8, "2027-02-20", 540, "https://www.gu.se/en/study-exchange", "2026-10-08", "Verified"),
        new(30, "University of Copenhagen", "Copenhagen", "Denmark", ["Business", "Economics", "Computer Science"], ["Bachelor", "Master"], ["English", "Danish"], 3.1, "2027-01-31", 540, "https://studies.ku.dk/visa/erasmus/", "2026-10-08", "Verified"),
        new(31, "University of Lisbon", "Lisbon", "Portugal", ["Engineering", "Business", "Arts"], ["Bachelor", "Master"], ["English", "Portuguese"], 2.7, "2027-03-05", 490, "https://www.ulisboa.pt/en/international", "2026-10-08", "NeedsReview"),
        new(32, "University of Milan", "Milan", "Italy", ["Engineering", "Economics", "Design"], ["Bachelor", "Master"], ["English", "Italian"], 2.8, "2027-03-01", 490, "https://www.unimi.it/en/international", "2026-10-08", "NeedsReview"),
        new(33, "University of Groningen", "Groningen", "Netherlands", ["Social Sciences", "Business", "Computer Science"], ["Bachelor", "Master"], ["English", "Dutch"], 2.9, "2027-02-15", 540, "https://www.rug.nl/education/exchange", "2026-10-08", "Verified"),
        new(34, "University of Innsbruck", "Innsbruck", "Austria", ["Engineering", "Business", "Physics"], ["Bachelor", "Master"], ["English", "German"], 2.8, "2027-03-01", 490, "https://www.uibk.ac.at/international/", "2026-10-08", "NeedsReview"),
        new(35, "University of Leuven", "Leuven", "Belgium", ["Medicine", "Business", "Engineering"], ["Bachelor", "Master"], ["English", "Dutch", "French"], 3.0, "2027-01-31", 540, "https://www.kuleuven.be/english/exchange", "2026-10-08", "Verified"),
        new(36, "University of Glasgow", "Glasgow", "United Kingdom", ["Computer Science", "Business", "Design"], ["Bachelor", "Master"], ["English"], 2.9, "2027-02-10", 540, "https://www.gla.ac.uk/exchange/", "2026-10-08", "Verified")
    ];

    public static ErasmusOpportunity[] AllOpportunities => Opportunities.Concat(ExpandedOpportunities).ToArray();

    private static readonly ErasmusOpportunity[] ExpandedOpportunities = BuildExpandedOpportunities();

    private static ErasmusOpportunity[] BuildExpandedOpportunities()
    {
        var candidates = new[]
        {
            ("University of Valencia", "Valencia", "Spain", "https://www.uv.es/uvweb/college/en/international-relations/incoming-students-1285846941830.html"),
            ("Pompeu Fabra University", "Barcelona", "Spain", "https://www.upf.edu/web/incoming"),
            ("Autonomous University of Barcelona", "Barcelona", "Spain", "https://www.uab.cat/web/international-exchange-1345680349341.html"),
            ("University of Seville", "Seville", "Spain", "https://www.us.es/en/international"),
            ("University of Granada", "Granada", "Spain", "https://internacional.ugr.es/pages/movilidad/estudiantes"),
            ("University of Salamanca", "Salamanca", "Spain", "https://rel-int.usal.es/en/"),
            ("University of Zaragoza", "Zaragoza", "Spain", "https://internacional.unizar.es/"),
            ("University of Malaga", "Malaga", "Spain", "https://www.uma.es/relaciones-internacionales/"),
            ("University of Santiago de Compostela", "Santiago de Compostela", "Spain", "https://www.usc.gal/en/international"),
            ("University of Coimbra", "Coimbra", "Portugal", "https://www.uc.pt/en/international-mobility"),
            ("NOVA University Lisbon", "Lisbon", "Portugal", "https://www.unl.pt/en/international"),
            ("University of Minho", "Braga", "Portugal", "https://www.uminho.pt/EN/international-mobility"),
            ("University of Aveiro", "Aveiro", "Portugal", "https://www.ua.pt/en/international"),
            ("University of Algarve", "Faro", "Portugal", "https://www.ualg.pt/en/international"),
            ("University of Padua", "Padua", "Italy", "https://www.unipd.it/en/international"),
            ("Sapienza University of Rome", "Rome", "Italy", "https://www.uniroma1.it/en/pagina/international-students"),
            ("University of Florence", "Florence", "Italy", "https://www.unifi.it/changelang-eng.html"),
            ("University of Pisa", "Pisa", "Italy", "https://www.unipi.it/index.php/eng"),
            ("University of Turin", "Turin", "Italy", "https://en.unito.it/international"),
            ("University of Naples Federico II", "Naples", "Italy", "https://www.unina.it/en_GB/international"),
            ("University of Trento", "Trento", "Italy", "https://www.unitn.it/en/international"),
            ("University of Pavia", "Pavia", "Italy", "https://en.unipv.it/"),
            ("Leiden University", "Leiden", "Netherlands", "https://www.universiteitleiden.nl/en/education/exchange"),
            ("Erasmus University Rotterdam", "Rotterdam", "Netherlands", "https://www.eur.nl/en/education/exchange"),
            ("Utrecht University", "Utrecht", "Netherlands", "https://www.uu.nl/en/education/exchange-and-study-abroad"),
            ("Radboud University", "Nijmegen", "Netherlands", "https://www.ru.nl/en/education/exchange"),
            ("University of Twente", "Enschede", "Netherlands", "https://www.utwente.nl/en/education/exchange/"),
            ("Maastricht University", "Maastricht", "Netherlands", "https://www.maastrichtuniversity.nl/education/exchange"),
            ("Tilburg University", "Tilburg", "Netherlands", "https://www.tilburguniversity.edu/students/study-abroad"),
            ("University of Freiburg", "Freiburg", "Germany", "https://www.tf.uni-freiburg.de/en/study-programs/international"),
            ("Heidelberg University", "Heidelberg", "Germany", "https://www.uni-heidelberg.de/en/study/international"),
            ("RWTH Aachen University", "Aachen", "Germany", "https://www.rwth-aachen.de/go/id/bqf/lidx/1"),
            ("University of Hamburg", "Hamburg", "Germany", "https://www.uni-hamburg.de/en/internationales.html"),
            ("University of Cologne", "Cologne", "Germany", "https://international.uni-koeln.de/en/"),
            ("TU Berlin", "Berlin", "Germany", "https://www.tu.berlin/en/international"),
            ("University of Bonn", "Bonn", "Germany", "https://www.uni-bonn.de/en/international"),
            ("University of Leipzig", "Leipzig", "Germany", "https://www.uni-leipzig.de/en/international"),
            ("University of Freiburg", "Freiburg", "Germany", "https://www.uni-freiburg.de/en/international/"),
            ("Lund University", "Lund", "Sweden", "https://www.lunduniversity.lu.se/international-admissions/exchange-studies"),
            ("Uppsala University", "Uppsala", "Sweden", "https://www.uu.se/en/study/exchange"),
            ("Linkoping University", "Linkoping", "Sweden", "https://liu.se/en/education/exchange-studies"),
            ("Umea University", "Umea", "Sweden", "https://www.umu.se/en/education/exchange-studies/"),
            ("University of Turku", "Turku", "Finland", "https://www.utu.fi/en/study-at-utu/exchange-studies"),
            ("Tampere University", "Tampere", "Finland", "https://www.tuni.fi/en/study-with-us/exchange-studies"),
            ("University of Oulu", "Oulu", "Finland", "https://www.oulu.fi/en/apply/exchange-studies"),
            ("University of Bergen", "Bergen", "Norway", "https://www.uib.no/en/education/exchange"),
            ("Norwegian University of Science and Technology", "Trondheim", "Norway", "https://www.ntnu.edu/studies/exchange"),
            ("University of Stavanger", "Stavanger", "Norway", "https://www.uis.no/en/international"),
            ("Aarhus University", "Aarhus", "Denmark", "https://international.au.dk/education/admissions/exchange"),
            ("University of Southern Denmark", "Odense", "Denmark", "https://www.sdu.dk/en/uddannelse/exchange"),
            ("Roskilde University", "Roskilde", "Denmark", "https://ruc.dk/en/international"),
            ("University of Strasbourg", "Strasbourg", "France", "https://www.unistra.fr/index.php?id=international"),
            ("University of Lyon", "Lyon", "France", "https://www.univ-lyon2.fr/international"),
            ("University of Bordeaux", "Bordeaux", "France", "https://www.u-bordeaux.com/International"),
            ("University of Montpellier", "Montpellier", "France", "https://www.umontpellier.fr/en/international"),
            ("University of Nantes", "Nantes", "France", "https://www.univ-nantes.fr/english-version/international"),
            ("University College Cork", "Cork", "Ireland", "https://www.ucc.ie/en/study/goingabroad/"),
            ("University of Galway", "Galway", "Ireland", "https://www.universityofgalway.ie/international-students/"),
            ("University College Dublin", "Dublin", "Ireland", "https://www.ucd.ie/global/studyabroad/"),
            ("University of Edinburgh", "Edinburgh", "United Kingdom", "https://www.ed.ac.uk/global/study-abroad"),
            ("University of Manchester", "Manchester", "United Kingdom", "https://www.manchester.ac.uk/study/international/study-abroad/"),
            ("University of Bristol", "Bristol", "United Kingdom", "https://www.bristol.ac.uk/global-opportunities/"),
            ("University of Leeds", "Leeds", "United Kingdom", "https://students.leeds.ac.uk/info/1010/study_abroad"),
            ("University of Sheffield", "Sheffield", "United Kingdom", "https://www.sheffield.ac.uk/global-opportunities"),
            ("University of Southampton", "Southampton", "United Kingdom", "https://www.southampton.ac.uk/international/exchange.page"),
            ("University of Krakow", "Krakow", "Poland", "https://international.uj.edu.pl/en_GB/"),
            ("Wroclaw University of Science and Technology", "Wroclaw", "Poland", "https://pwr.edu.pl/en/students/international"),
            ("Gdansk University of Technology", "Gdansk", "Poland", "https://pg.edu.pl/en/international"),
            ("Masaryk University", "Brno", "Czechia", "https://www.muni.cz/en/students/go-abroad"),
            ("Czech Technical University in Prague", "Prague", "Czechia", "https://international.cvut.cz/"),
            ("University of Vienna of Economics", "Vienna", "Austria", "https://www.wu.ac.at/en/international"),
            ("Graz University of Technology", "Graz", "Austria", "https://www.tugraz.at/en/studying-and-teaching/international"),
            ("University of Basel", "Basel", "Switzerland", "https://www.unibas.ch/en/Studies/International-Students.html"),
            ("University of Lausanne", "Lausanne", "Switzerland", "https://www.unil.ch/international/en/home.html"),
            ("University of Belgrade", "Belgrade", "Serbia", "https://www.bg.ac.rs/en/international/"),
            ("University of Novi Sad", "Novi Sad", "Serbia", "https://www.uns.ac.rs/index.php/en/international"),
            ("University of Sarajevo", "Sarajevo", "Bosnia and Herzegovina", "https://www.unsa.ba/en/international-cooperation"),
            ("University of Cyprus", "Nicosia", "Cyprus", "https://www.ucy.ac.cy/internationalrelations/en/"),
            ("University of Malta", "Msida", "Malta", "https://www.um.edu.mt/international"),
            ("University of Thessaloniki", "Thessaloniki", "Greece", "https://www.auth.gr/en/international/"),
            ("University of Crete", "Heraklion", "Greece", "https://www.uoc.gr/intrel/en/"),
            ("University of Bucharest Economics", "Bucharest", "Romania", "https://www.ase.ro/?page=international"),
            ("Babes-Bolyai University", "Cluj-Napoca", "Romania", "https://www.ubbcluj.ro/en/international"),
            ("University of Tallinn", "Tallinn", "Estonia", "https://www.tlu.ee/en/international"),
            ("University of Latvia", "Riga", "Latvia", "https://www.lu.lv/en/cooperation/international-cooperation/"),
            ("Kaunas University of Technology", "Kaunas", "Lithuania", "https://en.ktu.edu/international-relations/"),
            ("University of Maribor", "Maribor", "Slovenia", "https://www.um.si/en/international/"),
            ("University of Split", "Split", "Croatia", "https://www.unist.hr/en/international-relations"),
            ("University of Pecs", "Pecs", "Hungary", "https://international.pte.hu/"),
            ("University of Debrecen", "Debrecen", "Hungary", "https://edu.unideb.hu/p/international"),
            ("University of Bratislava Economics", "Bratislava", "Slovakia", "https://www.euba.sk/en/international-relations"),
            ("University of Iceland", "Reykjavik", "Iceland", "https://english.hi.is/study/exchange_students"),
            ("University of Luxembourg", "Luxembourg", "Luxembourg", "https://www.uni.lu/en/international/"),
            ("University of Andorra", "Sant Julia de Loria", "Andorra", "https://www.uda.ad/en/international/"),
        };

        var fields = new[] { new[] { "Computer Science", "Engineering", "Business" }, new[] { "Business", "Economics", "Social Sciences" }, new[] { "Design", "Arts", "Architecture" }, new[] { "Medicine", "Life Sciences", "Social Sciences" } };
        return candidates.Select((candidate, index) => new ErasmusOpportunity(
            37 + index,
            candidate.Item1,
            candidate.Item2,
            candidate.Item3,
            fields[index % fields.Length],
            ["Bachelor", "Master"],
            ["English"],
            2.5 + (index % 8) * 0.1,
            $"2027-{(index % 3) + 1:00}-{15 + (index % 12):00}",
            index % 3 == 0 ? 540 : 490,
            candidate.Item4,
            "2026-10-09",
            "NeedsReview")).ToArray();
    }
}
