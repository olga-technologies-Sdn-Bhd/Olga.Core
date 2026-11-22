using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Olga.Core.Application;

namespace Olga.Core.Worker;

public sealed class ProductNotificationOutboxEventPublisher(
    IOutboxEventPublisher inner,
    NpgsqlDataSource dataSource) : IOutboxEventPublisher
{
    public async Task PublishAsync(ClaimedOutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        if (Handles(outboxEvent.EventType)) await ProjectAsync(outboxEvent, cancellationToken);
        await inner.PublishAsync(outboxEvent, cancellationToken);
    }

    private static bool Handles(string eventType) => eventType is
        "ConnectionRequestCreated.v1" or "ConnectionAccepted.v1" or "RelationshipIntroductionRequested.v1";

    private async Task ProjectAsync(ClaimedOutboxEvent value, CancellationToken ct)
    {
        using var payload=JsonDocument.Parse(value.PayloadJson);
        var root=payload.RootElement;
        var purpose=value.EventType switch
        {
            "ConnectionRequestCreated.v1" => "CONNECTION_REQUEST",
            "ConnectionAccepted.v1" => "CONNECTION_ACCEPTED",
            _ => "INTRODUCTION_REQUEST"
        };
        var memberId=value.EventType switch
        {
            "ConnectionRequestCreated.v1" => Text(root,"recipient_member_id"),
            "RelationshipIntroductionRequested.v1" => Text(root,"recipient_member_id"),
            _ => null
        };
        await using var connection=await dataSource.OpenConnectionAsync(ct);
        if (value.EventType=="ConnectionAccepted.v1")
        {
            await using var find=new NpgsqlCommand("""
                SELECT request.sender_member_id
                FROM social.connection connection
                JOIN social.connection_request request ON request.connection_request_id=connection.accepted_request_id
                WHERE connection.connection_id=@resource_id
                """,connection);
            Add(find,"resource_id",value.AggregateId); memberId=(string?)await find.ExecuteScalarAsync(ct);
        }
        if (string.IsNullOrWhiteSpace(memberId)) return;
        await using var command=new NpgsqlCommand("""
            SELECT * FROM notification.try_enqueue(
              @notification_id,@member_id,@purpose,'PUSH',@resource_type,@resource_id,@template_code,@dedupe_key,'GENERAL',NULL)
            """,connection);
        Add(command,"notification_id",StableId("not",value.OutboxEventId)); Add(command,"member_id",memberId);
        Add(command,"purpose",purpose); Add(command,"resource_type",value.AggregateType); Add(command,"resource_id",value.AggregateId);
        Add(command,"template_code",purpose); Add(command,"dedupe_key",$"{purpose.ToLowerInvariant()}:{value.AggregateId}");
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string? Text(JsonElement root,string name) => root.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
    private static string StableId(string prefix,string value) => $"{prefix}_{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32]}";
    private static void Add(NpgsqlCommand command,string name,string value) => command.Parameters.Add(name,NpgsqlDbType.Varchar).Value=value;
}

public sealed class ProductNotificationDeliveryWorker(
    NpgsqlDataSource dataSource,
    IPushSender sender,
    IIdentityProtector identityProtector,
    IChatPushStore chatPushStore,
    TimeProvider timeProvider,
    ILogger<ProductNotificationDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var work=await ClaimAsync(stoppingToken);
                if (work is null) { await Task.Delay(TimeSpan.FromSeconds(10),timeProvider,stoppingToken); continue; }
                await DeliverAsync(work,stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception,"Product notification delivery failed; the queued notification remains retryable.");
                await Task.Delay(TimeSpan.FromSeconds(10),timeProvider,stoppingToken);
            }
        }
    }

    private async Task<NotificationWork?> ClaimAsync(CancellationToken ct)
    {
        await using var connection=await dataSource.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        string notificationId,purpose,resourceType,resourceId; short attemptCount,maxAttempts;
        await using (var command=new NpgsqlCommand("""
            SELECT notification.notification_id,notification.purpose_code,
                   COALESCE(notification.resource_type,'RESOURCE'),COALESCE(notification.resource_id,notification.notification_id),
                   notification.attempt_count,policy.max_attempts
            FROM notification.notification
            JOIN notification.notification_policy policy ON policy.notification_policy_id=notification.notification_policy_id
            WHERE notification.channel='PUSH' AND notification.status='PENDING'
              AND notification.scheduled_at<=CURRENT_TIMESTAMP AND notification.expires_at>CURRENT_TIMESTAMP
              AND notification.attempt_count<policy.max_attempts
              AND NOT EXISTS (SELECT 1 FROM notification.notification_delivery_attempt attempt
                  WHERE attempt.notification_id=notification.notification_id AND attempt.next_attempt_at>CURRENT_TIMESTAMP)
            ORDER BY notification.scheduled_at,notification.created_at
            FOR UPDATE OF notification SKIP LOCKED LIMIT 1
            """,connection,transaction))
        {
            await using var reader=await command.ExecuteReaderAsync(ct); if(!await reader.ReadAsync(ct)){await transaction.CommitAsync(ct);return null;}
            notificationId=reader.GetString(0);purpose=reader.GetString(1);resourceType=reader.GetString(2);resourceId=reader.GetString(3);
            attemptCount=reader.GetInt16(4);maxAttempts=reader.GetInt16(5);
        }
        var targets=new List<NotificationTarget>();
        await using (var command=new NpgsqlCommand("""
            SELECT token.push_token_id,token.device_id,token.provider,token.token_ciphertext
            FROM notification.notification item
            JOIN iam.member_device device ON device.member_id=item.member_id AND device.status='ACTIVE'
            JOIN notification.push_token token ON token.device_id=device.device_id AND token.status='ACTIVE'
            WHERE item.notification_id=@notification_id ORDER BY token.push_token_id
            """,connection,transaction))
        {
            Add(command,"notification_id",notificationId); await using var reader=await command.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct)) targets.Add(new(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),reader.GetFieldValue<byte[]>(3)));
        }
        if(targets.Count==0)
        {
            await using var failed=new NpgsqlCommand("UPDATE notification.notification SET status='FAILED',failure_code='NO_ACTIVE_DEVICE',updated_at=CURRENT_TIMESTAMP WHERE notification_id=@notification_id",connection,transaction);
            Add(failed,"notification_id",notificationId);await failed.ExecuteNonQueryAsync(ct);await transaction.CommitAsync(ct);return null;
        }
        var attemptIds=new List<(long TokenId,long AttemptId)>();
        for(var index=0;index<targets.Count;index++)
        {
            await using var attempt=new NpgsqlCommand("""
                INSERT INTO notification.notification_delivery_attempt(notification_id,attempt_number,provider,push_token_id,status,next_attempt_at)
                VALUES(@notification_id,@attempt_number,@provider,@push_token_id,'STARTED',CURRENT_TIMESTAMP+interval '2 minutes')
                RETURNING delivery_attempt_id
                """,connection,transaction);
            Add(attempt,"notification_id",notificationId);attempt.Parameters.AddWithValue("attempt_number",(short)(attemptCount+index+1));
            Add(attempt,"provider",targets[index].Provider);attempt.Parameters.AddWithValue("push_token_id",targets[index].PushTokenId);
            attemptIds.Add((targets[index].PushTokenId,(long)(await attempt.ExecuteScalarAsync(ct)??0L)));
        }
        await using (var update=new NpgsqlCommand("UPDATE notification.notification SET attempt_count=attempt_count+@count,updated_at=CURRENT_TIMESTAMP WHERE notification_id=@notification_id",connection,transaction))
        { update.Parameters.AddWithValue("count",(short)targets.Count);Add(update,"notification_id",notificationId);await update.ExecuteNonQueryAsync(ct); }
        await transaction.CommitAsync(ct);
        return new(notificationId,purpose,resourceType,resourceId,maxAttempts,targets,attemptIds.ToDictionary(x=>x.TokenId,x=>x.AttemptId));
    }

    private async Task DeliverAsync(NotificationWork work,CancellationToken ct)
    {
        var delivered=false;
        foreach(var target in work.Targets)
        {
            PushResult result;
            try
            {
                var token=identityProtector.Unprotect(target.DeviceId,target.Provider,target.TokenCiphertext);
                result=await sender.SendAsync(target.Provider,token,Content(work),ct);
            }
            catch(CryptographicException){result=PushResult.TokenInvalid;}
            delivered|=result==PushResult.Delivered;
            if(result==PushResult.Delivered) await chatPushStore.RecordResultAsync(target.PushTokenId,true,ct);
            else if(result==PushResult.TokenInvalid) await chatPushStore.RecordResultAsync(target.PushTokenId,false,ct);
            await RecordAttemptAsync(work.AttemptIds[target.PushTokenId],result,ct);
        }
        await using var command=dataSource.CreateCommand("""
            UPDATE notification.notification SET
              status=CASE WHEN @delivered THEN 'DELIVERED' WHEN attempt_count>=@max_attempts THEN 'FAILED' ELSE 'PENDING' END,
              sent_at=CASE WHEN @delivered THEN COALESCE(sent_at,CURRENT_TIMESTAMP) ELSE sent_at END,
              delivered_at=CASE WHEN @delivered THEN CURRENT_TIMESTAMP ELSE delivered_at END,
              failure_code=CASE WHEN @delivered THEN NULL WHEN attempt_count>=@max_attempts THEN 'PROVIDER_FAILED' ELSE failure_code END,
              updated_at=CURRENT_TIMESTAMP
            WHERE notification_id=@notification_id
            """);
        command.Parameters.AddWithValue("delivered",delivered);command.Parameters.AddWithValue("max_attempts",work.MaxAttempts);Add(command,"notification_id",work.NotificationId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task RecordAttemptAsync(long attemptId,PushResult result,CancellationToken ct)
    {
        await using var command=dataSource.CreateCommand("""
            UPDATE notification.notification_delivery_attempt SET
              status=CASE WHEN @delivered THEN 'DELIVERED' ELSE 'FAILED' END,
              acknowledged_at=CURRENT_TIMESTAMP,next_attempt_at=CASE WHEN @delivered THEN NULL ELSE CURRENT_TIMESTAMP+interval '1 minute' END,
              failure_code=CASE WHEN @delivered THEN NULL WHEN @invalid THEN 'TOKEN_INVALID' ELSE 'PROVIDER_FAILED' END
            WHERE delivery_attempt_id=@attempt_id
            """);
        command.Parameters.AddWithValue("delivered",result==PushResult.Delivered);command.Parameters.AddWithValue("invalid",result==PushResult.TokenInvalid);
        command.Parameters.AddWithValue("attempt_id",attemptId);await command.ExecuteNonQueryAsync(ct);
    }

    private static PushContent Content(NotificationWork work)
    {
        var body=work.Purpose switch
        {
            "CONNECTION_REQUEST"=>"New connection request",
            "CONNECTION_ACCEPTED"=>"Your connection request was accepted",
            "INTRODUCTION_REQUEST"=>"New introduction to confirm",
            "SCI_GOAL_MILESTONE"=>"You reached an SCI milestone",
            "SCI_HIGH_MATCH"=>"A relevant contact is ready",
            _=>"You have an OLGA update"
        };
        return new("OLGA",body,new Dictionary<string,string>{{"type",work.Purpose.ToLowerInvariant()},{"resource_type",work.ResourceType},{"resource_id",work.ResourceId}});
    }

    private static void Add(NpgsqlCommand command,string name,string value)=>command.Parameters.Add(name,NpgsqlDbType.Varchar).Value=value;
    private sealed record NotificationTarget(long PushTokenId,string DeviceId,string Provider,byte[] TokenCiphertext);
    private sealed record NotificationWork(string NotificationId,string Purpose,string ResourceType,string ResourceId,short MaxAttempts,
        IReadOnlyList<NotificationTarget> Targets,IReadOnlyDictionary<long,long> AttemptIds);
}
