using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Olga.Core.Application;
using Olga.Core.Domain;
using Contracts = Olga.Core.Contracts;

namespace Olga.Core.Infrastructure;

public sealed class CoreDbContext(DbContextOptions<CoreDbContext> options) : DbContext(options), ICoreStore
{
    bool ICoreStore.IsRelational => Database.IsRelational();
    public DbSet<MemberProfile> MemberProfiles => Set<MemberProfile>();
    public DbSet<ConsentPolicy> ConsentPolicies => Set<ConsentPolicy>();
    public DbSet<MemberConsent> MemberConsents => Set<MemberConsent>();
    public DbSet<EventRecord> EventRecords => Set<EventRecord>();
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<EventRegistration> EventRegistrations => Set<EventRegistration>();
    public DbSet<LiveModeSession> LiveModeSessions => Set<LiveModeSession>();
    public DbSet<EventPresence> EventPresences => Set<EventPresence>();
    public DbSet<ConnectionRequest> SocialConnectionRequests => Set<ConnectionRequest>();
    public DbSet<Connection> SocialConnections => Set<Connection>();
    public DbSet<MemberBlock> MemberBlocks => Set<MemberBlock>();
    public DbSet<Conversation> ChatConversations => Set<Conversation>();
    public DbSet<ConversationParticipant> ConversationParticipants => Set<ConversationParticipant>();
    public DbSet<Message> ChatMessages => Set<Message>();
    public DbSet<MessageReceipt> MessageReceipts => Set<MessageReceipt>();
    public DbSet<NotificationPreference> Preferences => Set<NotificationPreference>();
    public DbSet<PrivacyRequest> MemberPrivacyRequests => Set<PrivacyRequest>();
    public DbSet<SyncChange> Changes => Set<SyncChange>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();

    IQueryable<MemberProfile> ICoreStore.Profiles => MemberProfiles;
    IQueryable<ConsentPolicy> ICoreStore.ConsentPolicies => ConsentPolicies;
    IQueryable<MemberConsent> ICoreStore.Consents => MemberConsents;
    IQueryable<EventRecord> ICoreStore.Events => EventRecords;
    IQueryable<Venue> ICoreStore.Venues => Venues;
    IQueryable<EventRegistration> ICoreStore.Registrations => EventRegistrations;
    IQueryable<LiveModeSession> ICoreStore.LiveSessions => LiveModeSessions;
    IQueryable<EventPresence> ICoreStore.Presence => EventPresences;
    IQueryable<ConnectionRequest> ICoreStore.ConnectionRequests => SocialConnectionRequests;
    IQueryable<Connection> ICoreStore.Connections => SocialConnections;
    IQueryable<MemberBlock> ICoreStore.Blocks => MemberBlocks;
    IQueryable<Conversation> ICoreStore.Conversations => ChatConversations;
    IQueryable<ConversationParticipant> ICoreStore.ConversationParticipants => ConversationParticipants;
    IQueryable<Message> ICoreStore.Messages => ChatMessages;
    IQueryable<MessageReceipt> ICoreStore.MessageReceipts => MessageReceipts;
    IQueryable<NotificationPreference> ICoreStore.NotificationPreferences => Preferences;
    IQueryable<PrivacyRequest> ICoreStore.PrivacyRequests => MemberPrivacyRequests;
    IQueryable<SyncChange> ICoreStore.SyncChanges => Changes;

    void ICoreStore.Add<T>(T entity) => Set<T>().Add(entity);
    void ICoreStore.Remove<T>(T entity) => Set<T>().Remove(entity);
    Task ICoreStore.SaveAsync(CancellationToken ct) => SaveChangesAsync(ct);

    async Task ICoreStore.CreateMemberAsync(NewMemberRegistration member, CancellationToken ct)
    {
        if (!Database.IsRelational())
        {
            if (await MemberProfiles.AnyAsync(x => x.MemberId == member.MemberId, ct)) return;
            MemberProfiles.Add(new MemberProfile
            {
                MemberId = member.MemberId,
                DisplayName = member.DisplayName,
                Headline = member.Headline,
                Biography = member.ProfessionalSummary,
                Sector = member.RoleCategory,
                Visibility = member.Visibility,
                CompletenessScore = member.CompletenessScore,
                Status = "DRAFT"
            });
            await SaveChangesAsync(ct);
            return;
        }

        var connection = (NpgsqlConnection)Database.GetDbConnection();
        var close = await OpenIfNeededAsync(ct);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using (var audit = CreateCommand("SELECT ops.set_audit_context(@actor_id)", transaction))
            {
                AddVarchar(audit, "actor_id", member.MemberId, 64);
                await audit.ExecuteNonQueryAsync(ct);
            }

            await using var account = CreateCommand("""
                INSERT INTO iam.member(member_id, community_id, status, locale, verified_at)
                VALUES (@member_id, @community_id, 'ACTIVE', @locale,
                        CASE WHEN @has_verified_identity THEN CURRENT_TIMESTAMP ELSE NULL END)
                ON CONFLICT (member_id) DO NOTHING
                """, transaction);
            AddVarchar(account, "member_id", member.MemberId, 64);
            AddVarchar(account, "community_id", member.CommunityId, 64);
            AddVarchar(account, "locale", member.Locale, 16);
            account.Parameters.Add(new NpgsqlParameter("has_verified_identity", NpgsqlDbType.Boolean) { Value = member.Identities.Any(x => x.IsVerified) });
            if (await account.ExecuteNonQueryAsync(ct) == 0)
            {
                await transaction.CommitAsync(ct);
                return;
            }

            foreach (var identity in member.Identities)
            {
                await using var identityCommand = CreateCommand("""
                    INSERT INTO iam.member_identity(member_id, provider, provider_subject_hash, provider_subject_ciphertext, display_hint, is_primary, status, verified_at)
                    VALUES (@member_id, @provider, @subject_hash, @subject_ciphertext, @display_hint, @is_primary, 'ACTIVE',
                            CASE WHEN @is_verified THEN CURRENT_TIMESTAMP ELSE NULL END)
                    """, transaction);
                AddVarchar(identityCommand, "member_id", member.MemberId, 64);
                AddVarchar(identityCommand, "provider", identity.Provider, 32);
                AddChar(identityCommand, "subject_hash", identity.SubjectHash);
                identityCommand.Parameters.Add(new NpgsqlParameter("subject_ciphertext", NpgsqlDbType.Bytea) { Value = identity.SubjectCiphertext });
                AddVarchar(identityCommand, "display_hint", identity.DisplayHint, 80);
                identityCommand.Parameters.Add(new NpgsqlParameter("is_primary", NpgsqlDbType.Boolean) { Value = identity.IsPrimary });
                identityCommand.Parameters.Add(new NpgsqlParameter("is_verified", NpgsqlDbType.Boolean) { Value = identity.IsVerified });
                await identityCommand.ExecuteNonQueryAsync(ct);
            }

            await using var profile = CreateCommand("""
                INSERT INTO core.member_profile(member_id, display_name, headline, professional_summary, role_category, profile_status, visibility, completeness_score)
                VALUES (@member_id, @display_name, @headline, @professional_summary, @role_category, 'DRAFT', @visibility, @completeness_score)
                """, transaction);
            AddVarchar(profile, "member_id", member.MemberId, 64);
            AddVarchar(profile, "display_name", member.DisplayName, 150);
            AddNullableVarchar(profile, "headline", member.Headline, 240);
            AddNullableText(profile, "professional_summary", member.ProfessionalSummary);
            AddNullableVarchar(profile, "role_category", member.RoleCategory, 64);
            AddVarchar(profile, "visibility", member.Visibility, 20);
            profile.Parameters.Add(new NpgsqlParameter("completeness_score", NpgsqlDbType.Numeric) { Value = member.CompletenessScore });
            await profile.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "ux_member_identity_provider_subject_hash")
        {
            throw new DomainException("MEMBER_IDENTITY_ALREADY_REGISTERED", 409);
        }
        finally { if (close) await Database.CloseConnectionAsync(); }
    }

    async Task ICoreStore.EnsureMemberAsync(string memberId, CancellationToken ct)
    {
        if (!Database.IsRelational())
        {
            if (await MemberProfiles.AnyAsync(x => x.MemberId == memberId, ct)) return;
            MemberProfiles.Add(new MemberProfile { MemberId = memberId, Status = "DRAFT", Visibility = "HIDDEN" });
            await SaveChangesAsync(ct);
            return;
        }

        await using var command = CreateCommand("""
            INSERT INTO core.member_profile
                (member_id, display_name, profile_status, visibility, completeness_score, row_version, created_at, updated_at)
            VALUES
                (@member_id, '', 'DRAFT', 'HIDDEN', 0, 1, @created_at, @created_at)
            ON CONFLICT (member_id) DO NOTHING
            """);
        AddVarchar(command, "member_id", memberId, 64);
        AddTimestamp(command, "created_at", DateTimeOffset.UtcNow);
        var close = await OpenIfNeededAsync(ct);
        try { await command.ExecuteNonQueryAsync(ct); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation && ex.ConstraintName == "fk_member_profile_member_id")
        {
            throw new DomainException("MEMBER_NOT_REGISTERED", 404);
        }
        finally { if (close) await Database.CloseConnectionAsync(); }
    }

    async Task<(string ConnectionId, string ConversationId)> ICoreStore.AcceptConnectionRequestAsync(string requestId, string recipientId, string connectionId, string conversationId, string idempotencyKey, string requestHash, CancellationToken ct)
    {
        await using var command = CreateCommand("SELECT * FROM social.accept_connection_request(@request_id, @recipient_id, @connection_id, @conversation_id, @idempotency_key, @request_hash, @idempotency_expires_at, @sync_expires_at)");
        AddVarchar(command, "request_id", requestId, 64); AddVarchar(command, "recipient_id", recipientId, 64);
        AddVarchar(command, "connection_id", connectionId, 64); AddVarchar(command, "conversation_id", conversationId, 64);
        AddVarchar(command, "idempotency_key", idempotencyKey, 128); AddChar(command, "request_hash", requestHash);
        AddTimestamp(command, "idempotency_expires_at", DateTimeOffset.UtcNow.AddHours(24)); AddTimestamp(command, "sync_expires_at", DateTimeOffset.UtcNow.AddDays(30));
        var close = await OpenIfNeededAsync(ct);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("accept_connection_request returned no result.");
            return (reader.GetString(0), reader.GetString(1));
        }
        finally { if (close) await Database.CloseConnectionAsync(); }
    }

    async Task<Message> ICoreStore.SaveMessageAsync(string memberId, string conversationId, Contracts.MessageCreateRequest request, string idempotencyKey, string requestHash, CancellationToken ct)
    {
        await using var command = CreateCommand("SELECT * FROM chat.save_message(@message_id, @conversation_id, @sender_id, @message_type, @body, @client_sent_at, @idempotency_key, @request_hash, @idempotency_expires_at, @sync_expires_at)");
        AddVarchar(command, "message_id", request.MessageId, 64); AddVarchar(command, "conversation_id", conversationId, 64); AddVarchar(command, "sender_id", memberId, 64); AddVarchar(command, "message_type", request.MessageType, 20);
        command.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Text) { Value = request.Body is null ? DBNull.Value : request.Body });
        command.Parameters.Add(new NpgsqlParameter("client_sent_at", NpgsqlDbType.TimestampTz) { Value = request.ClientSentAt is null ? DBNull.Value : request.ClientSentAt.Value.ToUniversalTime() });
        AddVarchar(command, "idempotency_key", idempotencyKey, 128); AddChar(command, "request_hash", requestHash);
        AddTimestamp(command, "idempotency_expires_at", DateTimeOffset.UtcNow.AddHours(24)); AddTimestamp(command, "sync_expires_at", DateTimeOffset.UtcNow.AddDays(30));
        var close = await OpenIfNeededAsync(ct);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("save_message returned no result.");
            return new Message
            {
                MessageId = reader.GetString(reader.GetOrdinal("message_id")), ConversationId = reader.GetString(reader.GetOrdinal("conversation_id")), SenderMemberId = reader.GetString(reader.GetOrdinal("sender_member_id")),
                MessageType = reader.GetString(reader.GetOrdinal("message_type")), Body = GetNullableString(reader, "body"), ClientSentAt = GetNullableInstant(reader, "client_sent_at"),
                ServerSequence = reader.GetInt64(reader.GetOrdinal("server_sequence")), ModerationStatus = reader.GetString(reader.GetOrdinal("moderation_status")), DeletedAt = GetNullableInstant(reader, "deleted_at"),
                CreatedAt = GetInstant(reader, "created_at"), UpdatedAt = GetInstant(reader, "updated_at"), RowVersion = reader.GetInt64(reader.GetOrdinal("row_version"))
            };
        }
        finally { if (close) await Database.CloseConnectionAsync(); }
    }

    async Task<MessageReceipt> ICoreStore.SaveMessageReceiptAsync(string memberId, string messageId, Contracts.MessageReceiptRequest request, string idempotencyKey, string requestHash, CancellationToken ct)
    {
        await using var command = CreateCommand("SELECT * FROM chat.save_message_receipt(@message_id, @member_id, @delivered_at, @read_at, @idempotency_key, @request_hash, @idempotency_expires_at, @sync_expires_at)");
        AddVarchar(command, "message_id", messageId, 64); AddVarchar(command, "member_id", memberId, 64);
        command.Parameters.Add(new NpgsqlParameter("delivered_at", NpgsqlDbType.TimestampTz) { Value = request.DeliveredAt is null ? DBNull.Value : request.DeliveredAt.Value.ToUniversalTime() });
        command.Parameters.Add(new NpgsqlParameter("read_at", NpgsqlDbType.TimestampTz) { Value = request.ReadAt is null ? DBNull.Value : request.ReadAt.Value.ToUniversalTime() });
        AddVarchar(command, "idempotency_key", idempotencyKey, 128); AddChar(command, "request_hash", requestHash);
        AddTimestamp(command, "idempotency_expires_at", DateTimeOffset.UtcNow.AddHours(24)); AddTimestamp(command, "sync_expires_at", DateTimeOffset.UtcNow.AddDays(30));
        var close = await OpenIfNeededAsync(ct);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("save_message_receipt returned no result.");
            return new MessageReceipt { MessageId = reader.GetString(reader.GetOrdinal("message_id")), MemberId = reader.GetString(reader.GetOrdinal("member_id")), DeliveredAt = GetNullableInstant(reader, "delivered_at"), ReadAt = GetNullableInstant(reader, "read_at"), UpdatedAt = GetInstant(reader, "updated_at") };
        }
        finally { if (close) await Database.CloseConnectionAsync(); }
    }

    private NpgsqlCommand CreateCommand(string sql, NpgsqlTransaction? transaction = null) => new(sql, (NpgsqlConnection)Database.GetDbConnection(), transaction);
    private async Task<bool> OpenIfNeededAsync(CancellationToken ct) { var close = Database.GetDbConnection().State != ConnectionState.Open; if (close) await Database.OpenConnectionAsync(ct); return close; }
    private static void AddVarchar(NpgsqlCommand command, string name, string value, int size) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Varchar) { Size = size, Value = value });
    private static void AddNullableVarchar(NpgsqlCommand command, string name, string? value, int size) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Varchar) { Size = size, Value = value is null ? DBNull.Value : value });
    private static void AddNullableText(NpgsqlCommand command, string name, string? value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = value is null ? DBNull.Value : value });
    private static void AddChar(NpgsqlCommand command, string name, string value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Char) { Size = 64, Value = value });
    private static void AddTimestamp(NpgsqlCommand command, string name, DateTimeOffset value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value.ToUniversalTime() });
    private static string? GetNullableString(DbDataReader reader, string name) { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? null : reader.GetString(i); }
    private static DateTimeOffset GetInstant(DbDataReader reader, string name) => new(DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal(name)), DateTimeKind.Utc));
    private static DateTimeOffset? GetNullableInstant(DbDataReader reader, string name) { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(i), DateTimeKind.Utc)); }

    protected override void ConfigureConventions(ModelConfigurationBuilder conventions)
    {
        // PostgreSQL timestamptz preserves UTC instants at microsecond precision; bit maps to boolean.
        conventions.Properties<DateTimeOffset>().HaveColumnType("timestamp with time zone").HavePrecision(6);
        conventions.Properties<DateTimeOffset?>().HaveColumnType("timestamp with time zone").HavePrecision(6);
        conventions.Properties<bool>().HaveColumnType("boolean");
        conventions.Properties<bool?>().HaveColumnType("boolean");
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<MemberProfile>(e => { e.ToTable("member_profile", "core"); e.HasKey(x => x.MemberId); e.Property(x => x.MemberId).HasMaxLength(64); e.Property(x => x.DisplayName).HasMaxLength(150); e.Property(x => x.Headline).HasMaxLength(240); e.Property(x => x.Biography).HasColumnName("professional_summary").HasMaxLength(2000); e.Property(x => x.Sector).HasColumnName("role_category").HasMaxLength(64); e.Property(x => x.Status).HasColumnName("profile_status").HasMaxLength(24); e.Property(x => x.Visibility).HasMaxLength(20); e.Property(x => x.CompletenessScore).HasPrecision(5, 2); ConfigureVersion(e.Property(x => x.Version).HasColumnName("row_version")); });
        model.Entity<ConsentPolicy>(e => { e.ToTable("consent_policy", "consent"); e.HasKey(x => x.PolicyId); e.Property(x => x.PolicyId).HasMaxLength(64); e.Property(x => x.PurposeCode).HasMaxLength(64); e.Property(x => x.Version).HasMaxLength(32); e.Property(x => x.ContentHash).HasColumnType("character(64)").IsFixedLength(); ConfigureVersion(e.Property(x => x.RowVersion)); });
        model.Entity<MemberConsent>(e => { e.ToTable("member_consent", "consent"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasColumnName("member_consent_id").UseIdentityByDefaultColumn(); e.Property(x => x.MemberId).HasMaxLength(64); e.Property(x => x.PolicyId).HasMaxLength(64); e.Property(x => x.EvidenceJson).HasColumnType("jsonb"); e.HasIndex(x => new { x.MemberId, x.PolicyId, x.CapturedAt }); });
        model.Entity<EventRecord>(e => { e.ToTable("event", "event"); e.HasKey(x => x.EventId); e.Property(x => x.EventId).HasMaxLength(64); e.Property(x => x.CommunityId).HasMaxLength(64); e.Property(x => x.VenueId).HasMaxLength(64); e.Property(x => x.Name).HasMaxLength(250); e.Property(x => x.Status).HasMaxLength(24); ConfigureVersion(e.Property(x => x.RowVersion)); e.HasIndex(x => new { x.CommunityId, x.Status, x.StartsAt }); });
        model.Entity<Venue>(e => { e.ToTable("venue", "event"); e.HasKey(x => x.VenueId); e.Property(x => x.VenueId).HasMaxLength(64); e.Property(x => x.Name).HasMaxLength(200); e.Property(x => x.City).HasMaxLength(120); e.Property(x => x.Region).HasMaxLength(120); e.Property(x => x.CountryCode).HasColumnType("character(2)").IsFixedLength(); e.Property(x => x.TimezoneId).HasMaxLength(64); e.Property(x => x.Status).HasMaxLength(16); ConfigureVersion(e.Property(x => x.RowVersion)); });
        model.Entity<EventRegistration>(e => { e.ToTable("event_registration", "event"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasColumnName("event_registration_id").UseIdentityByDefaultColumn(); ConfigureVersion(e.Property(x => x.RowVersion)); e.HasIndex(x => new { x.EventId, x.MemberId }).IsUnique(); });
        model.Entity<LiveModeSession>(e => { e.ToTable("live_mode_session", "event"); e.HasKey(x => x.SessionId); e.Property(x => x.SessionId).HasColumnName("live_session_id").HasMaxLength(64); e.Property(x => x.StartedAt).HasColumnName("activated_at"); e.Property(x => x.RevokedAt).HasColumnName("disabled_at"); ConfigureVersion(e.Property(x => x.RowVersion)); e.HasIndex(x => new { x.EventId, x.MemberId, x.Status }); });
        model.Entity<EventPresence>(e => { e.ToTable("event_presence", "event"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasColumnName("presence_id").UseIdentityByDefaultColumn(); e.Property(x => x.SessionId).HasColumnName("live_session_id").HasMaxLength(64); e.Property(x => x.CoarseCell).HasMaxLength(32); e.HasIndex(x => new { x.SessionId, x.ExpiresAt }); });
        model.Entity<ConnectionRequest>(e => { e.ToTable("connection_request", "social"); e.HasKey(x => x.RequestId); e.Property(x => x.RequestId).HasColumnName("connection_request_id").HasMaxLength(64); ConfigureVersion(e.Property(x => x.RowVersion)); e.HasIndex(x => new { x.RecipientMemberId, x.Status, x.ExpiresAt }); });
        model.Entity<Connection>(e => { e.ToTable("connection", "social"); e.HasKey(x => x.ConnectionId); e.Property(x => x.ConnectionId).HasMaxLength(64); ConfigureVersion(e.Property(x => x.RowVersion)); e.HasIndex(x => new { x.MemberLowId, x.MemberHighId }).IsUnique(); });
        model.Entity<MemberBlock>(e => { e.ToTable("member_block", "social"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasColumnName("block_id").UseIdentityByDefaultColumn(); e.HasIndex(x => new { x.BlockerMemberId, x.BlockedMemberId }); });
        model.Entity<Conversation>(e => { e.ToTable("conversation", "chat"); e.HasKey(x => x.ConversationId); e.Property(x => x.ConversationId).HasMaxLength(64); ConfigureVersion(e.Property(x => x.RowVersion)); e.HasIndex(x => x.ConnectionId).IsUnique(); });
        model.Entity<ConversationParticipant>(e => { e.ToTable("conversation_participant", "chat"); e.HasKey(x => new { x.ConversationId, x.MemberId }); });
        model.Entity<Message>(e => { e.ToTable("message", "chat"); e.HasKey(x => x.MessageId); e.Property(x => x.MessageId).HasMaxLength(64); e.Property(x => x.Body).HasColumnType("text"); ConfigureVersion(e.Property(x => x.RowVersion)); e.HasIndex(x => new { x.ConversationId, x.ServerSequence }).IsUnique(); });
        model.Entity<MessageReceipt>(e => { e.ToTable("message_receipt", "chat"); e.HasKey(x => new { x.MessageId, x.MemberId }); });
        model.Entity<NotificationPreference>(e => { e.ToTable("notification_preference", "notification"); e.HasKey(x => new { x.MemberId, x.PurposeCode }); ConfigureVersion(e.Property(x => x.RowVersion)); });
        model.Entity<PrivacyRequest>(e => { e.ToTable("privacy_request", "consent"); e.HasKey(x => x.PrivacyRequestId); e.Property(x => x.PrivacyRequestId).HasMaxLength(64); ConfigureVersion(e.Property(x => x.RowVersion)); e.HasIndex(x => new { x.MemberId, x.CreatedAt }); });
        model.Entity<SyncChange>(e => { e.ToTable("sync_change", "ops"); e.HasKey(x => x.SyncSequence); e.Property(x => x.SyncSequence).ValueGeneratedOnAdd(); e.Property(x => x.PayloadJson).HasColumnType("jsonb"); e.HasIndex(x => new { x.MemberScopeId, x.SyncSequence }); });
        model.Entity<OutboxEvent>(e => { e.ToTable("outbox_event", "ops"); e.HasKey(x => x.OutboxEventId); e.Property(x => x.OutboxEventId).HasMaxLength(64); e.Property(x => x.PayloadJson).HasColumnType("jsonb"); e.HasIndex(x => new { x.PublishedAt, x.OccurredAt }); });

        foreach (var entity in model.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                if (property.GetColumnName() == property.Name) property.SetColumnName(ToSnakeCase(property.Name));
    }

    private static void ConfigureVersion(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<long> property) =>
        property.HasColumnType("bigint").HasDefaultValue(1L).IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();

    private static string ToSnakeCase(string value) => string.Concat(value.Select((c, i) => i > 0 && char.IsUpper(c) ? $"_{char.ToLowerInvariant(c)}" : char.ToLowerInvariant(c).ToString()));
}

public static class LocalDevelopmentSeeder
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
            if (await db.MemberProfiles.AnyAsync(ct)) return;
            db.MemberProfiles.AddRange(
                new MemberProfile { MemberId = "A123", DisplayName = "Asha Rao", Headline = "Pharmaceutical founder", Sector = "pharmaceutical", Status = "ACTIVE" },
                new MemberProfile { MemberId = "B456", DisplayName = "Ben Lim", Headline = "Cold-chain operator", Sector = "logistics", Status = "ACTIVE" },
                new MemberProfile { MemberId = "D111", DisplayName = "Dana Lee", Headline = "Distribution advisor", Sector = "distribution", Status = "ACTIVE" });
            db.ConsentPolicies.AddRange(
                new ConsentPolicy { PolicyId = "live-mode-v1", PurposeCode = "LIVE_MODE", Version = "1", ContentHash = new string('0', 64), EffectiveFrom = DateTimeOffset.UtcNow.AddYears(-1) },
                new ConsentPolicy { PolicyId = "matching-v1", PurposeCode = "MATCHING", Version = "1", ContentHash = new string('1', 64), EffectiveFrom = DateTimeOffset.UtcNow.AddYears(-1) });
            db.EventRecords.Add(new EventRecord { EventId = "event-001", Name = "OLGA Connect Pilot", StartsAt = DateTimeOffset.UtcNow.AddDays(-1), EndsAt = DateTimeOffset.UtcNow.AddDays(30), LiveModeEnabled = true });
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }
}
