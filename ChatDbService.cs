using Microsoft.Data.SqlClient;
using ChatBot.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ChatBot.Controllers;

namespace ChatBot.Services
{
    public class ChatDbService
    {
        private readonly string _connectionString;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<ChatDbService> _logger;
        private readonly string _interviewVideoFolder;
        private readonly string _userTimeZone;

        public ChatDbService(IConfiguration config, IHttpContextAccessor httpContextAccessor, ILogger<ChatDbService> logger)
        {
            _connectionString = config.GetConnectionString("DefaultConnection");
            _interviewVideoFolder = config.GetSection("UploadPaths:InterviewVideoFolder").Value;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
            _userTimeZone = config.GetValue<string>("UserTimeZone") ?? "UTC";
        }

        private DateTime ConvertToUtc(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Unspecified)
            {
                return TimeZoneInfo.ConvertTimeToUtc(dateTime, TimeZoneInfo.Local);
            }
            return dateTime.ToUniversalTime();
        }

        private DateTime ConvertToLocalTime(DateTime utcDateTime)
        {
            try
            {
                var timeZone = TimeZoneInfo.FindSystemTimeZoneById(_userTimeZone);
                return TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, timeZone);
            }
            catch (TimeZoneNotFoundException)
            {
                _logger.LogWarning("Time zone {TimeZone} not found. Falling back to UTC.", _userTimeZone);
                return utcDateTime;
            }
        }

        public void SaveUserDetails(UserDetails user)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var checkCmd = new SqlCommand("SELECT COUNT(*) FROM Users WHERE UserId = @UserId", conn);
                checkCmd.Parameters.AddWithValue("@UserId", user.UserId ?? (object)DBNull.Value);
                bool userExists = (int)checkCmd.ExecuteScalar() > 0;

                SqlCommand cmd;
                if (userExists)
                {
                    cmd = new SqlCommand(@"
                        UPDATE Users
                        SET Name = @Name, Phone = @Phone, Email = @Email, Experience = @Experience, 
                            EmploymentStatus = @EmploymentStatus, Reason = @Reason, CreatedAt = @CreatedAt, 
                            IDProofPath = @IDProofPath, IDProofType = @IDProofType, 
                            ResumePath = @ResumePath, ResumeType = @ResumeType, ResumeScore = @ResumeScore
                        WHERE UserId = @UserId", conn);
                }
                else
                {
                    cmd = new SqlCommand(@"
                        INSERT INTO Users (UserId, Name, Phone, Email, Experience, EmploymentStatus, Reason, 
                            CreatedAt, IDProofPath, IDProofType, ResumePath, ResumeType, ResumeScore)
                        VALUES (@UserId, @Name, @Phone, @Email, @Experience, @EmploymentStatus, @Reason, 
                            @CreatedAt, @IDProofPath, @IDProofType, @ResumePath, @ResumeType, @ResumeScore)", conn);
                }

                cmd.Parameters.AddWithValue("@UserId", user.UserId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Name", user.Name ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Phone", user.Phone ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Email", user.Email ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Experience", user.Experience ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@EmploymentStatus", user.EmploymentStatus ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Reason", user.Reason ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedAt", ConvertToUtc(user.CreatedAt == default ? DateTime.UtcNow : user.CreatedAt));
                cmd.Parameters.AddWithValue("@IDProofPath", user.IDProofPath ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@IDProofType", user.IDProofType ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ResumePath", user.ResumePath ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ResumeType", user.ResumeType ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ResumeScore", user.ResumeScore.HasValue ? user.ResumeScore.Value : (object)DBNull.Value);

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving user details for UserId: {UserId}", user.UserId);
                throw;
            }
        }

        public int GetInterviewAttemptCount(string name, string email, string phone, DateTime? dateOfBirth)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    SELECT COUNT(*) 
                    FROM Interactions 
                    WHERE Questions IS NOT NULL AND IsComplete = 1
                    AND UserId IN (
                        SELECT UserId 
                        FROM Users 
                        WHERE (Name = @Name OR @Name IS NULL)
                        AND (Email = @Email OR @Email IS NULL)
                        AND (Phone = @Phone OR @Phone IS NULL)
                    )", conn);

                cmd.Parameters.AddWithValue("@Name", string.IsNullOrEmpty(name) ? (object)DBNull.Value : name);
                cmd.Parameters.AddWithValue("@Email", string.IsNullOrEmpty(email) ? (object)DBNull.Value : email);
                cmd.Parameters.AddWithValue("@Phone", string.IsNullOrEmpty(phone) ? (object)DBNull.Value : phone);

                return (int)cmd.ExecuteScalar();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error counting interview attempts for Name: {Name}, Email: {Email}, Phone: {Phone}", name, email, phone);
                throw;
            }
        }

        public void MarkInterviewAsSubmitted(int interactionId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    UPDATE Interactions 
                    SET IsSubmitted = 1
                    WHERE InteractionId = @InteractionId AND Questions IS NOT NULL", conn);

                cmd.Parameters.AddWithValue("@InteractionId", interactionId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking interview session as submitted for InteractionId: {InteractionId}", interactionId);
                throw;
            }
        }

        public void SaveMessage(ChatMessage message)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    INSERT INTO Interactions (UserId, UserMessage, BotResponse, Model, CreatedAt)
                    VALUES (@UserId, @UserMessage, @BotResponse, @Model, @CreatedAt)", conn);

                cmd.Parameters.AddWithValue("@UserId", message.UserId ?? "");
                cmd.Parameters.AddWithValue("@UserMessage", message.UserMessage ?? "");
                cmd.Parameters.AddWithValue("@BotResponse", message.BotResponse ?? "");
                cmd.Parameters.AddWithValue("@Model", message.Model ?? "custom");
                cmd.Parameters.AddWithValue("@CreatedAt", ConvertToUtc(message.CreatedAt));

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving message for UserId: {UserId}", message.UserId);
                throw;
            }
        }

        public InterviewSession? GetLatestSession(string userId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    SELECT TOP 1 * FROM Interactions 
                    WHERE UserId = @UserId AND Questions IS NOT NULL
                    ORDER BY CreatedAt DESC", conn);
                cmd.Parameters.AddWithValue("@UserId", userId);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var utcCreatedAt = (DateTime)reader["CreatedAt"];
                    return new InterviewSession
                    {
                        Id = (int)reader["InteractionId"],
                        UserId = (string)reader["UserId"],
                        JobTitle = reader["JobTitle"] != DBNull.Value ? (string)reader["JobTitle"] : "",
                        QuestionIndex = reader["QuestionIndex"] != DBNull.Value ? (int)reader["QuestionIndex"] : 0,
                        Questions = JsonConvert.DeserializeObject<List<string>>((string)reader["Questions"] ?? "[]") ?? new(),
                        Answers = JsonConvert.DeserializeObject<List<string>>((string)reader["Answers"] ?? "[]") ?? new(),
                        IsComplete = reader["IsComplete"] != DBNull.Value ? (bool)reader["IsComplete"] : false,
                        IsSubmitted = reader["IsSubmitted"] != DBNull.Value ? (bool)reader["IsSubmitted"] : false,
                        TabSwitchCount = reader["TabSwitchCount"] != DBNull.Value ? (int)reader["TabSwitchCount"] : 0,
                        VideoPath = reader["VideoPath"] != DBNull.Value ? (string)reader["VideoPath"] : null,
                        InterviewScore = reader["InterviewScore"] != DBNull.Value ? (decimal?)reader["InterviewScore"] : null,
                        CreatedAt = ConvertToLocalTime(utcCreatedAt)
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving latest session for UserId: {UserId}", userId);
                throw;
            }
        }

        public void UpdateInterviewScore(int interactionId, float score)
        {
            if (score < 0 || score > 100)
            {
                throw new ArgumentException("Interview score must be between 0 and 100.", nameof(score));
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    UPDATE Interactions
                    SET InterviewScore = @InterviewScore
                    WHERE InteractionId = @InteractionId AND Questions IS NOT NULL", conn);

                cmd.Parameters.AddWithValue("@InteractionId", interactionId);
                cmd.Parameters.AddWithValue("@InterviewScore", score);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating interview score for InteractionId: {InteractionId}", interactionId);
                throw;
            }
        }

        public float? GetInterviewScore(int interactionId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    SELECT InterviewScore
                    FROM Interactions
                    WHERE InteractionId = @InteractionId AND Questions IS NOT NULL", conn);

                cmd.Parameters.AddWithValue("@InteractionId", interactionId);
                var result = cmd.ExecuteScalar();
                return result != DBNull.Value ? (float?)result : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving interview score for InteractionId: {InteractionId}", interactionId);
                throw;
            }
        }

        public void UpdateInterviewSession(InterviewSession session)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                if (!string.IsNullOrEmpty(session.VideoPath) && !session.VideoPath.Contains(_interviewVideoFolder))
                {
                    session.VideoPath = Path.Combine(Directory.GetCurrentDirectory(), _interviewVideoFolder, session.VideoPath);
                    _logger.LogInformation("Normalized VideoPath to: {VideoPath}", session.VideoPath);
                }

                var cmd = new SqlCommand(@"
                    UPDATE Interactions
                    SET QuestionIndex = @QuestionIndex,
                        Questions = @Questions,
                        Answers = @Answers,
                        IsComplete = @IsComplete,
                        IsSubmitted = @IsSubmitted,
                        TabSwitchCount = @TabSwitchCount,
                        VideoPath = @VideoPath,
                        CreatedAt = @CreatedAt
                    WHERE InteractionId = @InteractionId AND Questions IS NOT NULL", conn);

                cmd.Parameters.AddWithValue("@InteractionId", session.Id);
                cmd.Parameters.AddWithValue("@QuestionIndex", session.QuestionIndex);
                cmd.Parameters.AddWithValue("@Questions", JsonConvert.SerializeObject(session.Questions));
                cmd.Parameters.AddWithValue("@Answers", JsonConvert.SerializeObject(session.Answers));
                cmd.Parameters.AddWithValue("@IsComplete", session.IsComplete);
                cmd.Parameters.AddWithValue("@IsSubmitted", session.IsSubmitted);
                cmd.Parameters.AddWithValue("@TabSwitchCount", session.TabSwitchCount);
                cmd.Parameters.AddWithValue("@VideoPath", session.VideoPath ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedAt", ConvertToUtc(session.CreatedAt == default ? DateTime.UtcNow : session.CreatedAt));

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating interview session for InteractionId: {InteractionId}", session.Id);
                throw;
            }
        }

        public void UpdateTabSwitchCount(string userId, int count)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    UPDATE Interactions 
                    SET TabSwitchCount = COALESCE(TabSwitchCount, 0) + @Count
                    WHERE UserId = @UserId 
                    AND Questions IS NOT NULL 
                    AND IsComplete = 0
                    AND InteractionId = (
                        SELECT MAX(InteractionId)
                        FROM Interactions
                        WHERE UserId = @UserId 
                        AND Questions IS NOT NULL 
                        AND IsComplete = 0
                    )", conn);

                cmd.Parameters.AddWithValue("@UserId", userId);
                cmd.Parameters.AddWithValue("@Count", count);
                int rowsAffected = cmd.ExecuteNonQuery();

                if (rowsAffected == 0)
                {
                    _logger.LogWarning("No active interview session found for UserId: {UserId}", userId);
                }
                else
                {
                    _logger.LogInformation("Tab switch count incremented by {Count} for UserId: {UserId}", count, userId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating tab switch count for UserId: {UserId}", userId);
            }
        }

        public void SaveFullConversation(string userId, string name, string phone, string email, List<ChatMessage> messages)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                foreach (var message in messages)
                {
                    message.CreatedAt = ConvertToUtc(message.CreatedAt);
                }

                var conversationText = JsonConvert.SerializeObject(messages);
                var cmd = new SqlCommand(
                    @"INSERT INTO Interactions (
                        UserId, ConversationText, CreatedAt
                    ) VALUES (
                        @UserId, @ConversationText, @CreatedAt
                    )", conn);

                cmd.Parameters.AddWithValue("@UserId", userId);
                cmd.Parameters.AddWithValue("@ConversationText", conversationText);
                cmd.Parameters.AddWithValue("@CreatedAt", ConvertToUtc(DateTime.Now));

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error saving full conversation for UserId: {userId}", ex);
            }
        }

        public void SaveInterviewSession(InterviewSession session)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var checkCmd = new SqlCommand(
                    "SELECT COUNT(*) FROM Interactions WHERE InteractionId = @InteractionId", conn);
                checkCmd.Parameters.AddWithValue("@InteractionId", session.Id);
                bool sessionExists = (int)checkCmd.ExecuteScalar() > 0;

                SqlCommand cmd;

                if (sessionExists)
                {
                    cmd = new SqlCommand(@"
                        UPDATE Interactions
                        SET UserId = @UserId, JobTitle = @JobTitle, QuestionIndex = @QuestionIndex, 
                            Questions = @Questions, Answers = @Answers, IsComplete = @IsComplete, 
                            IsSubmitted = @IsSubmitted, TabSwitchCount = @TabSwitchCount, 
                            VideoPath = @VideoPath, InterviewScore = @InterviewScore, CreatedAt = @CreatedAt
                        WHERE InteractionId = @InteractionId", conn);

                    cmd.Parameters.AddWithValue("@InteractionId", session.Id);
                }
                else
                {
                    cmd = new SqlCommand(@"
                        INSERT INTO Interactions (UserId, JobTitle, QuestionIndex, Questions, 
                            Answers, IsComplete, IsSubmitted, TabSwitchCount, VideoPath, InterviewScore, CreatedAt)
                        VALUES (@UserId, @JobTitle, @QuestionIndex, @Questions, @Answers, 
                            @IsComplete, @IsSubmitted, @TabSwitchCount, @VideoPath, @InterviewScore, @CreatedAt);
                        SELECT SCOPE_IDENTITY();", conn);
                }

                cmd.Parameters.AddWithValue("@UserId", session.UserId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@JobTitle", session.JobTitle ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@QuestionIndex", session.QuestionIndex);
                cmd.Parameters.AddWithValue("@Questions", JsonConvert.SerializeObject(session.Questions) ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Answers", JsonConvert.SerializeObject(session.Answers) ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@IsComplete", session.IsComplete);
                cmd.Parameters.AddWithValue("@IsSubmitted", session.IsSubmitted);
                cmd.Parameters.AddWithValue("@TabSwitchCount", session.TabSwitchCount);
                cmd.Parameters.AddWithValue("@VideoPath", session.VideoPath ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@InterviewScore", session.InterviewScore.HasValue ? session.InterviewScore.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedAt", ConvertToUtc(session.CreatedAt == default ? DateTime.UtcNow : session.CreatedAt));

                if (sessionExists)
                {
                    cmd.ExecuteNonQuery();
                }
                else
                {
                    session.Id = Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving interview session for InteractionId: {InteractionId}", session.Id);
                throw;
            }
        }

        public void SaveMessageTemplate(string templateName, string messageType, string templateContent, bool isDefault)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                var cmd = new SqlCommand(@"
                    INSERT INTO MessageTemplates (TemplateName, MessageType, TemplateContent, IsDefault, CreatedAt, UpdatedAt)
                    VALUES (@TemplateName, @MessageType, @TemplateContent, @IsDefault, @CreatedAt, @UpdatedAt)", conn);
                cmd.Parameters.AddWithValue("@TemplateName", templateName);
                cmd.Parameters.AddWithValue("@MessageType", messageType);
                cmd.Parameters.AddWithValue("@TemplateContent", templateContent);
                cmd.Parameters.AddWithValue("@IsDefault", isDefault);
                cmd.Parameters.AddWithValue("@CreatedAt", ConvertToUtc(DateTime.Now));
                cmd.Parameters.AddWithValue("@UpdatedAt", ConvertToUtc(DateTime.Now));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving message template: {TemplateName}", templateName);
                throw;
            }
        }

        public void UpdateMessageTemplate(int templateId, string templateName, string messageType, string templateContent, bool isDefault)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                var cmd = new SqlCommand(@"
                    UPDATE MessageTemplates
                    SET TemplateName = @TemplateName, MessageType = @MessageType, TemplateContent = @TemplateContent, 
                        IsDefault = @IsDefault, UpdatedAt = @UpdatedAt
                    WHERE TemplateId = @TemplateId", conn);
                cmd.Parameters.AddWithValue("@TemplateId", templateId);
                cmd.Parameters.AddWithValue("@TemplateName", templateName);
                cmd.Parameters.AddWithValue("@MessageType", messageType);
                cmd.Parameters.AddWithValue("@TemplateContent", templateContent);
                cmd.Parameters.AddWithValue("@IsDefault", isDefault);
                cmd.Parameters.AddWithValue("@UpdatedAt", ConvertToUtc(DateTime.Now));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating message template: {TemplateId}", templateId);
                throw;
            }
        }

        public List<MessageTemplate> GetMessageTemplates()
        {
            try
            {
                var templates = new List<MessageTemplate>();
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                var cmd = new SqlCommand("SELECT TemplateName, MessageType, TemplateContent, IsDefault, CreatedAt, UpdatedAt FROM MessageTemplates", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    templates.Add(new MessageTemplate
                    {
                        TemplateName = reader["TemplateName"] != DBNull.Value ? reader["TemplateName"].ToString() : string.Empty,
                        MessageType = reader["MessageType"] != DBNull.Value ? reader["MessageType"].ToString() : string.Empty,
                        TemplateContent = reader["TemplateContent"] != DBNull.Value ? reader["TemplateContent"].ToString() : string.Empty,
                        IsDefault = reader["IsDefault"] != DBNull.Value ? (bool)reader["IsDefault"] : false,
                    });
                }
                _logger.LogInformation("Retrieved {Count} message templates", templates.Count);
                return templates;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving message templates");
                return new List<MessageTemplate>();
            }
        }

        public void UpdateUserMessageStatus(string userId, bool emailSent, bool whatsappSent)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                var cmd = new SqlCommand("UPDATE Users SET EmailSent = @EmailSent, WhatsAppSent = @WhatsAppSent WHERE UserId = @UserId", conn);
                cmd.Parameters.AddWithValue("@UserId", userId);
                cmd.Parameters.AddWithValue("@EmailSent", emailSent);
                cmd.Parameters.AddWithValue("@WhatsAppSent", whatsappSent);
                cmd.ExecuteNonQuery();
                _logger.LogInformation("Updated message status for UserId: {UserId}, EmailSent: {EmailSent}, WhatsAppSent: {WhatsAppSent}", userId, emailSent, whatsappSent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating message status for UserId: {UserId}", userId);
                throw;
            }
        }

        public (bool EmailSent, bool WhatsAppSent) GetUserMessageStatus(string userId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                var cmd = new SqlCommand("SELECT EmailSent, WhatsAppSent FROM Users WHERE UserId = @UserId", conn);
                cmd.Parameters.AddWithValue("@UserId", userId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return (
                        reader["EmailSent"] != DBNull.Value ? (bool)reader["EmailSent"] : false,
                        reader["WhatsAppSent"] != DBNull.Value ? (bool)reader["WhatsAppSent"] : false
                    );
                }
                return (false, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving message status for UserId: {UserId}", userId);
                throw;
            }
        }

        public void SavePersonalityTestResult(string userId, int interactionId, Dictionary<int, int> responses, Dictionary<string, float> report)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                var cmd = new SqlCommand(@"
                    INSERT INTO PersonalityTestResults (UserId, InteractionId, Responses, Report, CreatedAt)
                    VALUES (@UserId, @InteractionId, @Responses, @Report, @CreatedAt)", conn);
                cmd.Parameters.AddWithValue("@UserId", userId);
                cmd.Parameters.AddWithValue("@InteractionId", interactionId);
                cmd.Parameters.AddWithValue("@Responses", JsonConvert.SerializeObject(responses));
                cmd.Parameters.AddWithValue("@Report", JsonConvert.SerializeObject(report));
                cmd.Parameters.AddWithValue("@CreatedAt", ConvertToUtc(DateTime.Now));
                cmd.ExecuteNonQuery();
                _logger.LogInformation("Saved personality test result for UserId: {UserId}, InteractionId: {InteractionId}", userId, interactionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving personality test result for UserId: {UserId}, InteractionId: {InteractionId}", userId, interactionId);
                throw;
            }
        }

        public (Dictionary<int, int> Responses, Dictionary<string, float> Report)? GetPersonalityTestResult(string userId, int interactionId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                var cmd = new SqlCommand(@"
                    SELECT Responses, Report 
                    FROM PersonalityTestResults 
                    WHERE UserId = @UserId AND InteractionId = @InteractionId", conn);
                cmd.Parameters.AddWithValue("@UserId", userId);
                cmd.Parameters.AddWithValue("@InteractionId", interactionId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var responses = reader["Responses"] != DBNull.Value
                        ? JsonConvert.DeserializeObject<Dictionary<int, int>>((string)reader["Responses"])
                        : new Dictionary<int, int>();
                    var report = reader["Report"] != DBNull.Value
                        ? JsonConvert.DeserializeObject<Dictionary<string, float>>((string)reader["Report"])
                        : new Dictionary<string, float>();
                    return (responses, report);
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving personality test result for UserId: {UserId}, InteractionId: {InteractionId}", userId, interactionId);
                throw;
            }
        }

        public (Dictionary<int, int> Responses, Dictionary<string, float> Report)? GetLatestPersonalityTestResult(string userId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                var cmd = new SqlCommand(@"
            SELECT TOP 1 Responses, Report
            FROM PersonalityTestResults
            WHERE UserId = @UserId
            ORDER BY CreatedAt DESC", conn);
                cmd.Parameters.AddWithValue("@UserId", userId);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var responses = reader["Responses"] != DBNull.Value
                        ? JsonConvert.DeserializeObject<Dictionary<int, int>>((string)reader["Responses"])
                        : new Dictionary<int, int>();
                    var report = reader["Report"] != DBNull.Value
                        ? JsonConvert.DeserializeObject<Dictionary<string, float>>((string)reader["Report"])
                        : new Dictionary<string, float>();
                    return (responses, report);
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving latest personality test result for UserId: {UserId}", userId);
                throw;
            }
        }

        public void SaveCopyPasteEvent(CopyPasteEvent ev)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    INSERT INTO CopyPasteEvents (UserId, InteractionId, Type, Content, Timestamp, CreatedAt)
                    VALUES (@UserId, @InteractionId, @Type, @Content, @Timestamp, @CreatedAt)", conn);

                cmd.Parameters.AddWithValue("@UserId", ev.UserId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@InteractionId", ev.InteractionId);
                cmd.Parameters.AddWithValue("@Type", ev.Type ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Content", ev.Content ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Timestamp", ConvertToUtc(ev.Timestamp));
                cmd.Parameters.AddWithValue("@CreatedAt", ConvertToUtc(ev.CreatedAt));

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving copy-paste event for UserId: {UserId}, InteractionId: {InteractionId}", ev.UserId, ev.InteractionId);
                throw;
            }
        }

        //public void SaveScreenshotEvent(ScreenshotEvent ev)
        //{
        //    try
        //    {
        //        using var conn = new SqlConnection(_connectionString);
        //        conn.Open();

        //        var cmd = new SqlCommand(@"
        //            INSERT INTO ScreenshotEvents (UserId, InteractionId, Timestamp, CreatedAt)
        //            VALUES (@UserId, @InteractionId, @Timestamp, @CreatedAt)", conn);

        //        cmd.Parameters.AddWithValue("@UserId", ev.UserId ?? (object)DBNull.Value);
        //        cmd.Parameters.AddWithValue("@InteractionId", ev.InteractionId);
        //        cmd.Parameters.AddWithValue("@Timestamp", ConvertToUtc(ev.Timestamp));
        //        cmd.Parameters.AddWithValue("@CreatedAt", ConvertToUtc(ev.CreatedAt));

        //        cmd.ExecuteNonQuery();
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error saving screenshot event for UserId: {UserId}, InteractionId: {InteractionId}", ev.UserId, ev.InteractionId);
        //        throw;
        //    }
        //}

        public void SaveTabSwitchEvent(TabSwitchEvent ev)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    INSERT INTO TabSwitchEvents (UserId, InteractionId, Timestamp, Duration, CreatedAt)
                    VALUES (@UserId, @InteractionId, @Timestamp, @Duration, @CreatedAt)", conn);

                cmd.Parameters.AddWithValue("@UserId", ev.UserId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@InteractionId", ev.InteractionId);
                cmd.Parameters.AddWithValue("@Timestamp", ConvertToUtc(ev.Timestamp));
                cmd.Parameters.AddWithValue("@Duration", ev.Duration);
                cmd.Parameters.AddWithValue("@CreatedAt", ConvertToUtc(ev.CreatedAt));

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving tab switch event for UserId: {UserId}, InteractionId: {InteractionId}", ev.UserId, ev.InteractionId);
                throw;
            }
        }

        public InterviewSession? GetSessionById(int interactionId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
            SELECT * FROM Interactions 
            WHERE InteractionId = @InteractionId AND Questions IS NOT NULL", conn);
                cmd.Parameters.AddWithValue("@InteractionId", interactionId);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var utcCreatedAt = (DateTime)reader["CreatedAt"];
                    return new InterviewSession
                    {
                        Id = (int)reader["InteractionId"],
                        UserId = (string)reader["UserId"],
                        JobTitle = reader["JobTitle"] != DBNull.Value ? (string)reader["JobTitle"] : "",
                        QuestionIndex = reader["QuestionIndex"] != DBNull.Value ? (int)reader["QuestionIndex"] : 0,
                        Questions = JsonConvert.DeserializeObject<List<string>>((string)reader["Questions"] ?? "[]") ?? new(),
                        Answers = JsonConvert.DeserializeObject<List<string>>((string)reader["Answers"] ?? "[]") ?? new(),
                        IsComplete = reader["IsComplete"] != DBNull.Value ? (bool)reader["IsComplete"] : false,
                        IsSubmitted = reader["IsSubmitted"] != DBNull.Value ? (bool)reader["IsSubmitted"] : false,
                        TabSwitchCount = reader["TabSwitchCount"] != DBNull.Value ? (int)reader["TabSwitchCount"] : 0,
                        VideoPath = reader["VideoPath"] != DBNull.Value ? (string)reader["VideoPath"] : null,
                        InterviewScore = reader["InterviewScore"] != DBNull.Value ? (decimal?)reader["InterviewScore"] : null,
                        CreatedAt = ConvertToLocalTime(utcCreatedAt)
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving session for InteractionId: {InteractionId}", interactionId);
                throw;
            }
        }

    }
}