﻿using System.Net.Http.Headers;
using System.Text;
using BuildingBlocks.Pagination;
using ChatBox.API.Data;
using ChatBox.API.Dtos;
using ChatBox.API.Dtos.Gemini;
using ChatBox.API.Models;
using Google.Apis.Auth.OAuth2;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace ChatBox.API.Services;

public class GeminiService(
    IOptions<GeminiConfig> config,
    ChatBoxDbContext dbContext,
    SummarizationService summarizationService) 
{
    private readonly GeminiConfig _config = config.Value;
    private static readonly Dictionary<Guid, SemaphoreSlim> SessionLocks = new();
    private static readonly Lock LockDict = new();
    const int MaxUserInputLength = 1000;
    

    public async Task<List<AIMessageResponseDto>> SendMessageAsync(AIMessageRequestDto request, Guid userId)
    {
        await ValidateSessionOwnershipAsync(request.SessionId, userId);

        SemaphoreSlim sessionLock;
        lock (LockDict)
        {
            if (!SessionLocks.TryGetValue(request.SessionId, out sessionLock))
            {
                sessionLock = new SemaphoreSlim(1, 1);
                SessionLocks[request.SessionId] = sessionLock;
            }
        }

        await sessionLock.WaitAsync();
        try
        {
            var session = await dbContext.AIChatSessions
                .AsNoTracking()
                .FirstAsync(s => s.Id == request.SessionId);

            var contentParts = await LoadSessionHistoryMessages(request, session);

            var finalInput = BuildUserInputWithTruncation(request);

            contentParts.Add(new GeminiContentDto(
                "user", [new GeminiContentPartDto(finalInput)]
            ));
            
            var payload = BuildGeminiMessagePayload(contentParts);
            
            var responseText = await CallGeminiAPIAsync(payload);

            if (string.IsNullOrWhiteSpace(responseText))
                throw new Exception("Failed to get a response from Gemini.");

            var aiMessages = SplitGeminiResponse(request.SessionId, responseText);  
            
            await SaveMessagesAsync(request.SessionId, userId, request.UserMessage, request.SentAt, aiMessages);

            //Tự động tóm tắt nếu vượt ngưỡng
            await summarizationService.MaybeSummarizeSessionAsync(userId, request.SessionId);

            return aiMessages.Select(m => new AIMessageResponseDto(
                    m.SessionId,
                    m.SenderIsEmo,
                    m.Content,
                    m.CreatedDate))
                .ToList();
        }
        finally
        {
            sessionLock.Release();
        }
    }

    private static List<AIMessage> SplitGeminiResponse(Guid sessionId, string responseText)
    {
        var aiMessages = responseText
            .Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => new AIMessage
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                SenderUserId = null,
                SenderIsEmo = true,
                Content = part,
                CreatedDate = DateTime.UtcNow,
                IsRead = false
            })
            .ToList();
        
        return aiMessages;
    }

    private static string BuildUserInputWithTruncation(AIMessageRequestDto request)
    {
        var trimmedUserMessage = request.UserMessage.Length > MaxUserInputLength
            ? request.UserMessage[..MaxUserInputLength] + "..."
            : request.UserMessage;
            
        var notice = request.UserMessage.Length > MaxUserInputLength
            ? "Ghi chú: nội dung người dùng đã được rút gọn vì quá dài.\n"
            : "";

        var finalInput = notice + trimmedUserMessage;
        return finalInput;
    }

    private async Task<List<GeminiContentDto>> LoadSessionHistoryMessages(AIMessageRequestDto request, AIChatSession session)
    {
        //Lấy các message mới kể từ lần tóm tắt trước
        var lastIndex = session.LastSummarizedIndex ?? 0;

        var messages = await dbContext.AIChatMessages
            .Where(m => m.SessionId == request.SessionId)
            .OrderBy(m => m.CreatedDate)
            .Skip(lastIndex)
            .ToListAsync();

        var contentParts = new List<GeminiContentDto>();

        //Nếu có bản tóm tắt, bắt đầu từ đó
        if (!string.IsNullOrWhiteSpace(session.Summarization))
        {
            contentParts.Add(new GeminiContentDto(
                "user", [new GeminiContentPartDto($"Tóm tắt trước đó của cuộc hội thoại:\n{session.Summarization}")]
            ));
        }
        
        if (messages.Count == 0)
        {
            var lastSequenceIndex = await GetLastMessageSequenceIndex(request.SessionId);
            
            var lastMessageBlock = await dbContext.AIChatMessages
                .Where(m => m.SessionId == request.SessionId && m.BlockNumber == lastSequenceIndex)
                .OrderBy(m => m.CreatedDate)
                .ToListAsync();

            foreach (var message in lastMessageBlock)
            {
                contentParts.Add(new GeminiContentDto(
                    message.SenderIsEmo ? "model" : "user", [new GeminiContentPartDto($"{message.Content}")]
                ));
            }
        }

        //Thêm các message chưa tóm tắt vào
        foreach (var m in messages)
        {
            contentParts.Add(new GeminiContentDto(
                m.SenderIsEmo ? "model" : "user",
                [new GeminiContentPartDto(m.Content)]
            ));
        }

        return contentParts;
    }


    public async Task<PaginatedResult<AIMessageDto>> GetMessagesAsync(Guid sessionId, Guid userId,
        PaginationRequest paginationRequest)
    {
        await ValidateSessionOwnershipAsync(sessionId, userId);

        var pageIndex = paginationRequest.PageIndex;
        var pageSize = paginationRequest.PageSize;

        ValidatePaginationRequest(pageIndex, pageSize);

        var query = dbContext.AIChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.CreatedDate);

        var totalCount = await query.LongCountAsync();

        var messages = await query
            .Skip((pageIndex - 1) * pageSize)
            .Take(pageSize)
            .OrderBy(m => m.CreatedDate)
            .ProjectToType<AIMessageDto>()
            .ToListAsync();

        return new PaginatedResult<AIMessageDto>(pageIndex, pageSize, totalCount, messages);
    }

    private static void ValidatePaginationRequest(int pageIndex, int pageSize)
    {
        if (pageIndex <= 0 || pageSize <= 0)
            throw new ArgumentException("Tham số phân trang không hợp lệ.");
    }

    public async Task<AIMessage> AddMessageAsync(Guid sessionId, Guid userId, string content, bool senderIsEmo)
    {
        await ValidateSessionOwnershipAsync(sessionId, userId);

        var message = new AIMessage
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            SenderUserId = senderIsEmo ? null : userId,
            SenderIsEmo = senderIsEmo,
            Content = content,
            CreatedDate = DateTime.UtcNow,
            IsRead = false
        };

        dbContext.AIChatMessages.Add(message);
        await dbContext.SaveChangesAsync();
        return message;
    }

    public async Task MarkMessagesAsReadAsync(Guid sessionId, Guid userId)
    {
        await ValidateSessionOwnershipAsync(sessionId, userId);

        var unreadMessages = await dbContext.AIChatMessages
            .Where(m => m.SessionId == sessionId && !m.IsRead && !m.SenderIsEmo)
            .ToListAsync();

        foreach (var message in unreadMessages)
        {
            message.IsRead = true;
        }

        await dbContext.SaveChangesAsync();
    }

    // ========== PRIVATE HELPERS ==========

    private async Task ValidateSessionOwnershipAsync(Guid sessionId, Guid userId)
    {
        var session = await dbContext.AIChatSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId && s.IsActive == true);

        if (session == null)
            throw new UnauthorizedAccessException("Không tìm thấy phiên trò chuyện hoặc phiên trò chuyện này không thuộc về người dùng.");
    }

    private GeminiRequestDto BuildGeminiMessagePayload(List<GeminiContentDto> contents)
    {
        return new GeminiRequestDto(
            Contents: contents,
            SystemInstruction: new GeminiSystemInstructionDto(new GeminiContentPartDto(_config.SystemInstruction)),
            GenerationConfig: new GeminiGenerationConfigDto(
                Temperature: 1.0,
                TopP: 0.95,
                MaxOutputTokens: 8192
            ),
            SafetySettings:
            [
                new("HARM_CATEGORY_HATE_SPEECH"),
                new("HARM_CATEGORY_DANGEROUS_CONTENT"),
                new("HARM_CATEGORY_SEXUALLY_EXPLICIT"),
                new("HARM_CATEGORY_HARASSMENT")
            ]
        );
    }


    private async Task<string> GetAccessTokenAsync()
    {
        var credential = await GoogleCredential.GetApplicationDefaultAsync();
        credential = credential.CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        return await credential.UnderlyingCredential.GetAccessTokenForRequestAsync();
    }

    private async Task SaveMessagesAsync(Guid sessionId, Guid userId, string userMessage, DateTime userMessageSentAt,
        List<AIMessage> aiResponse)
    {
        var lastSeq = await GetLastMessageSequenceIndex(sessionId);
        
        var nextSeq = lastSeq + 1;
        
        dbContext.AIChatMessages.Add(
            new AIMessage
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                SenderUserId = userId,
                SenderIsEmo = false,
                Content = userMessage,
                CreatedDate = userMessageSentAt,
                IsRead = true,
                BlockNumber = nextSeq
            });

        foreach (var message in aiResponse)
        {
            message.BlockNumber = nextSeq;
        }

        dbContext.AIChatMessages.AddRange(aiResponse);

        await dbContext.SaveChangesAsync();
    }

    private async Task<int> GetLastMessageSequenceIndex(Guid sessionId)
    {
        var lastSeq = await dbContext.AIChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.BlockNumber)
            .Select(m => m.BlockNumber)
            .FirstOrDefaultAsync();
        
        return lastSeq;
    }


    private async Task<string> CallGeminiAPIAsync(GeminiRequestDto payload)
    {
        var token = await GetAccessTokenAsync();
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var url =
            $"https://{_config.Location}-aiplatform.googleapis.com/v1/projects/{_config.ProjectId}/locations/{_config.Location}/endpoints/{_config.EndpointId}:streamGenerateContent";
        var settings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy()
            }
        };
        var content = new StringContent(JsonConvert.SerializeObject(payload, settings), Encoding.UTF8, "application/json");
        //
        // var jsonPayload = JsonConvert.SerializeObject(payload, settings);
        // Console.WriteLine(jsonPayload);

        var response = await client.PostAsync(url, content);
        var result = await response.Content.ReadAsStringAsync();

        var array = JArray.Parse(result);

        var texts = array
            .Select(token => token["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString())
            .Where(text => !string.IsNullOrEmpty(text))
            .ToList();

        return string.Join("", texts);
    }
}