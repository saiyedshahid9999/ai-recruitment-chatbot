using ChatBot.Models;
using ChatBot.Services;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xceed.Words.NET;

namespace ChatBot.Controllers
{
    public class ChatController : Controller
    {
        private readonly ChatGPTService _chatGPTService;
        private readonly ChatDbService _chatDbService; 
        private readonly ILogger _logger;
        private readonly IConfiguration _configuration; 
        private readonly List<PreInterviewQuestion> _preInterviewQuestions;
        private readonly string _resumeFolder;
        private readonly string _idProofFolder;
        private readonly string _interviewVideoFolder;
        private readonly NotificationService _notificationService;
        private readonly string _userTimeZone;
        private readonly List<PersonalityQuestion> _personalityQuestions;

        public ChatController(
            ChatGPTService chatGPTService,
            ChatDbService chatDbService,
            ILogger<ChatController> logger,
            IConfiguration configuration)
        {
            _chatGPTService = chatGPTService;
            _chatDbService = chatDbService;
            _logger = logger;
            _configuration = configuration;
            _preInterviewQuestions = configuration.GetSection("PreInterviewQuestions")
                                                 .Get<List<PreInterviewQuestion>>();
            _resumeFolder = configuration.GetSection("UploadPaths:ResumeFolder").Value;
            _idProofFolder = configuration.GetSection("UploadPaths:IDProofFolder").Value;
            _interviewVideoFolder = configuration.GetSection("UploadPaths:InterviewVideoFolder").Value;
            _userTimeZone = configuration.GetValue<string>("UserTimeZone") ?? "UTC";
            _personalityQuestions = configuration.GetSection("PersonalityQuestions")
                                                .Get<List<PersonalityQuestion>>() ?? new List<PersonalityQuestion>();
        }

        public class PersonalityQuestion
        {
            public int Id { get; set; }
            public string Question { get; set; }
            public string Type { get; set; }
        }
        public class PreInterviewQuestion
        {
            public string State { get; set; }
            public string Prompt { get; set; }
            public string ValidationRegex { get; set; }
            public string ErrorMessage { get; set; }
            public bool SkipAllowed { get; set; }
            public string SkipToState { get; set; }
            public Dictionary<string, string> ConditionalNextStates { get; set; }
            public bool RequiresIDProof { get; set; }
            public bool RequiresResume { get; set; }
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

        [HttpGet]
        public IActionResult Index()
        {
            HttpContext.Session.SetString("SessionMessages", JsonConvert.SerializeObject(new List<ChatMessage>()));
            HttpContext.Session.SetString("UserIdentity", "");
            EnsureUserRecord(HttpContext.Session.Id);
            return View();
        }

        private void EnsureUserRecord(string userId)
        {
            try
            {
                var connectionString = _configuration.GetConnectionString("DefaultConnection");
                using var conn = new SqlConnection(connectionString);
                conn.Open();

                var checkCmd = new SqlCommand("SELECT COUNT(*) FROM Users WHERE UserId = @UserId", conn);
                checkCmd.Parameters.AddWithValue("@UserId", userId);
                bool userExists = (int)checkCmd.ExecuteScalar() > 0;

                if (!userExists)
                {
                    var user = new UserDetails
                    {
                        UserId = userId,
                        CreatedAt = DateTime.UtcNow
                    };
                    _chatDbService.SaveUserDetails(user);
                    _logger.LogInformation("Created new Users record for UserId: {UserId}", userId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ensuring Users record for UserId: {UserId}", userId);
                throw;
            }
        }

        [HttpPost]
        public async Task<IActionResult> SendMessage([FromBody] ChatMessage userMsg)
        {
            string msg = userMsg.UserMessage?.Trim() ?? "";
            string userId = HttpContext.Session.Id;
            string response = "";
            string modelUsed = "custom";
            bool startInterview = false;

            EnsureUserRecord(userId);

            var message = new ChatMessage
            {
                UserId = userId,
                UserMessage = msg,
                CreatedAt = DateTime.UtcNow
            };

            var sessionMessages = HttpContext.Session.GetString("SessionMessages") is string messagesStr
                ? JsonConvert.DeserializeObject<List<ChatMessage>>(messagesStr) ?? new List<ChatMessage>()
                : new List<ChatMessage>();
            var userIdentity = HttpContext.Session.GetString("UserIdentity");
            UserDetails userDetails = HttpContext.Session.GetString("UserDetails") is string userDetailsStr
                ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) ?? new UserDetails { UserId = userId }
                : new UserDetails { UserId = userId };

            _logger.LogInformation($"User query: {msg}");
            _logger.LogInformation($"IsJobIntentAsync: {await _chatGPTService.IsJobIntentAsync(msg)}");
            _logger.LogInformation($"IsLocationIntentAsync: {await _chatGPTService.IsLocationIntentAsync(msg)}");
            _logger.LogInformation($"IsCompanyRelated: {_chatGPTService.IsCompanyRelated(msg)}");
            _logger.LogInformation($"IsServiceRelated: {_chatGPTService.IsServiceRelated(msg)}");
            _logger.LogInformation($"IsProductRelated: {_chatGPTService.IsProductRelated(msg)}");
            _logger.LogInformation($"IsLocationRelated: {_chatGPTService.IsLocationRelated(msg)}");

            try
            {
                string ExtractName(string input)
                {
                    input = input.Trim();
                    var patterns = new[]
                    {
                        @"^(?:my name is|i am|name is|call me)\s+([a-zA-Z\s]+)$",
                        @"^([a-zA-Z\s]+)$"
                    };

                    foreach (var pattern in patterns)
                    {
                        var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
                        if (match.Success && match.Groups.Count > 1)
                        {
                            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(match.Groups[1].Value.Trim().ToLower());
                        }
                    }
                    return input;
                }

                void ClearApplicationState()
                {
                    HttpContext.Session.Remove("ApplicationState");
                    HttpContext.Session.Remove("UserDetails");
                    HttpContext.Session.Remove("SelectedJob");
                    HttpContext.Session.Remove("JobList");
                    HttpContext.Session.Remove("ResumeContent");
                    HttpContext.Session.Remove("InterviewRetakeCount");
                    HttpContext.Session.Remove("FirstInterviewSessionId");
                    HttpContext.Session.Remove("RetakeInterviewSessionId");
                    HttpContext.Session.Remove("PersonalityQuestionIndex");
                    HttpContext.Session.Remove("PersonalityResponses");
                    HttpContext.Session.Remove("PersonalityTestCompleted");
                }

                void SaveAndClearSessionMessages(string name = null, string phone = null, string email = null, bool finalizeIdentity = false)
                {
                    if (sessionMessages.Count > 0)
                    {
                        if (finalizeIdentity && string.IsNullOrEmpty(userIdentity))
                        {
                            if (!string.IsNullOrEmpty(name))
                            {
                                if (!string.IsNullOrEmpty(phone))
                                    userIdentity = $"{name.Replace(" ", "_")}_{phone.Replace(" ", "").Replace("-", "")}";
                                else if (!string.IsNullOrEmpty(email))
                                    userIdentity = $"{name.Replace(" ", "_")}_{email.Replace(" ", "").Replace("@", "_").Replace(".", "_")}";
                                else
                                    userIdentity = $"{name.Replace(" ", "_")}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                            }
                            else if (!string.IsNullOrEmpty(phone))
                            {
                                userIdentity = $"{userId}_{phone.Replace(" ", "").Replace("-", "")}";
                            }
                            else if (!string.IsNullOrEmpty(email))
                            {
                                userIdentity = $"{userId}_{email.Replace(" ", "").Replace("@", "_").Replace(".", "_")}";
                            }

                            if (!string.IsNullOrEmpty(userIdentity))
                            {
                                HttpContext.Session.SetString("UserIdentity", userIdentity);
                            }
                        }

                        foreach (var msg in sessionMessages)
                        {
                            msg.CreatedAt = DateTime.UtcNow;
                        }

                        _chatDbService.SaveFullConversation(userId, name, phone, email, sessionMessages);
                        sessionMessages.Clear();
                        HttpContext.Session.SetString("SessionMessages", JsonConvert.SerializeObject(sessionMessages));
                    }
                }

                async Task<string> HandleCompanyQuery(string query, string followUpPrompt = "")
                {
                    _logger.LogInformation($"Processing company query: {query}");
                    var (resp, model) = await _chatGPTService.GetSmartResponseAsync(query);
                    modelUsed = model;
                    return resp + (string.IsNullOrEmpty(followUpPrompt) ? "" : $"\n\n{followUpPrompt}");
                }

                Dictionary<int, int> GetPersonalityResponses()
                {
                    var responsesStr = HttpContext.Session.GetString("PersonalityResponses");
                    return string.IsNullOrEmpty(responsesStr)
                        ? new Dictionary<int, int>()
                        : JsonConvert.DeserializeObject<Dictionary<int, int>>(responsesStr) ?? new Dictionary<int, int>();
                }

                void SavePersonalityResponses(Dictionary<int, int> responses)
                {
                    HttpContext.Session.SetString("PersonalityResponses", JsonConvert.SerializeObject(responses));
                }

                Dictionary<string, float> GeneratePersonalityReport(Dictionary<int, int> responses)
                {
                    var report = new Dictionary<string, float>
                    {
                        { "Teamwork", (responses.GetValueOrDefault(1, 0) + responses.GetValueOrDefault(3, 0)) / 2.0f },
                        { "Stress Management", responses.GetValueOrDefault(2, 0) },
                        { "Leadership", responses.GetValueOrDefault(3, 0) },
                        { "Detail Orientation", responses.GetValueOrDefault(4, 0) },
                        { "Rule-Following", responses.GetValueOrDefault(5, 0) }
                    };
                    return report;
                }

                if (Regex.IsMatch(msg, @"\b(cancel|stop|quit|restart|start over)\b", RegexOptions.IgnoreCase))
                {
                    _logger.LogInformation("Cancel/restart intent detected.");
                    message.BotResponse = "Application process cancelled. How can I assist you now? To apply for a job, let me know you're interested in job openings or upload your resume.";
                    sessionMessages.Add(message);
                    _chatDbService.SaveMessage(message);
                    SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                    ClearApplicationState();
                    return Json(new { response = message.BotResponse, model = modelUsed, startInterview = false });
                }

                if (Regex.IsMatch(msg, @"\b(resume|cv|upload resume|upload cv|attach resume|attach cv)\b", RegexOptions.IgnoreCase))
                {
                    _logger.LogInformation("Resume upload intent detected.");
                    response = "Please upload your resume (PDF or Word format) using the upload button below the input area.";
                    message.BotResponse = response;
                    sessionMessages.Add(message);
                    _chatDbService.SaveMessage(message);
                    return Json(new { response, model = modelUsed, startInterview = false });
                }

                if (Regex.IsMatch(msg, @"\b(hi|hello|hey)\b", RegexOptions.IgnoreCase))
                {
                    _logger.LogInformation("Greeting intent detected.");
                    ClearApplicationState();
                    response = "👋 Hello! I’m the official company chatbot. How can I assist you today? Ask about our services, products, job openings, or upload your resume to find suitable jobs!";
                }
                else if (_chatDbService.GetLatestSession(userId) is InterviewSession session && !session.IsComplete && session.QuestionIndex < session.Questions.Count)
                {
                    _logger.LogInformation($"Continuing interview for {session.JobTitle}, question {session.QuestionIndex + 1}.");
                    if (Regex.IsMatch(msg, @"\b(when|end|finish|done)\b", RegexOptions.IgnoreCase))
                    {
                        int remainingQuestions = session.Questions.Count - session.QuestionIndex;
                        response = $"You have {remainingQuestions} question{(remainingQuestions != 1 ? "s" : "")} remaining in the interview for {session.JobTitle}.";
                    }
                    else if (_chatGPTService.IsCompanyRelated(msg))
                    {
                        response = await HandleCompanyQuery(msg, $"Continuing with your interview for {session.JobTitle}. ❓ Question {session.QuestionIndex + 1}: {session.Questions[session.QuestionIndex]}");
                    }
                    else
                    {
                        session.Answers.Add(msg);
                        session.QuestionIndex++;

                        if (session.QuestionIndex < session.Questions.Count)
                        {
                            response = $"❓ Question {session.QuestionIndex + 1}: {session.Questions[session.QuestionIndex]}";
                        }
                        else
                        {
                            session.IsComplete = true;
                            int retakeCount = HttpContext.Session.GetInt32("InterviewRetakeCount") ?? 0;
                            if (retakeCount == 0)
                            {
                                HttpContext.Session.SetString("FirstInterviewSessionId", session.Id.ToString());
                                response = $"✅ Thank you for completing the interview for the position of {session.JobTitle}. Would you like to retake the interview? You have one opportunity to retake. Reply 'retake' to start over or 'submit' to finalize your interview.";
                                HttpContext.Session.SetInt32("InterviewRetakeCount", retakeCount + 1);
                            }
                            else
                            {
                                HttpContext.Session.SetString("RetakeInterviewSessionId", session.Id.ToString());
                                response = $"✅ Thank you for completing your retake interview for the position of {session.JobTitle}. Please choose which interview to submit: reply 'first' to submit your first attempt or 'retake' to submit this retake.";
                            }
                            _chatDbService.UpdateInterviewSession(session);
                        }

                        _chatDbService.UpdateInterviewSession(session);
                    }
                }
                else if (HttpContext.Session.GetString("FirstInterviewSessionId") is string firstSessionId && HttpContext.Session.GetString("RetakeInterviewSessionId") is string retakeSessionId)
                {
                    InterviewSession selectedSession = null;
                    string selectedType = "";
                    string videoPathToDelete = null;

                    if (Regex.IsMatch(msg, @"\b(first)\b", RegexOptions.IgnoreCase))
                    {
                        selectedSession = _chatDbService.GetLatestSession(userId);
                        if (selectedSession?.Id != int.Parse(firstSessionId))
                        {
                            response = "❌ Invalid first interview session.";
                            goto EndInterviewHandling;
                        }
                        selectedType = "first";
                        // Get retake session to delete its video
                        var retakeSession = _chatDbService.GetLatestSession(userId); // Adjust to fetch by retakeSessionId if needed
                        if (retakeSession?.Id == int.Parse(retakeSessionId))
                        {
                            videoPathToDelete = retakeSession.VideoPath;
                        }
                    }
                    else if (Regex.IsMatch(msg, @"\b(retake)\b", RegexOptions.IgnoreCase))
                    {
                        selectedSession = _chatDbService.GetLatestSession(userId);
                        if (selectedSession?.Id != int.Parse(retakeSessionId))
                        {
                            response = "❌ Invalid retake interview session.";
                            goto EndInterviewHandling;
                        }
                        selectedType = "retake";
                        // Get first session to delete its video
                        var firstSession = _chatDbService.GetLatestSession(userId); // Adjust to fetch by firstSessionId if needed
                        if (firstSession?.Id == int.Parse(firstSessionId))
                        {
                            videoPathToDelete = firstSession.VideoPath;
                        }
                    }
                    else
                    {
                        response = "Please reply with 'first' to submit your first interview attempt or 'retake' to submit this retake.";
                        goto EndInterviewHandling;
                    }

                    float? interviewScore = await _chatGPTService.CalculateInterviewScoreAsync(
                        selectedSession.JobTitle,
                        selectedSession.Questions,
                        selectedSession.Answers);
                    if (interviewScore.HasValue)
                    {
                        _chatDbService.UpdateInterviewScore(selectedSession.Id, interviewScore.Value);
                        response = $"✅ Your {selectedType} interview for {selectedSession.JobTitle} has been submitted. Interview Score: {interviewScore.Value:F1}. Submission time: {ConvertToLocalTime(DateTime.UtcNow):yyyy-MM-dd HH:mm:ss}.";
                        _logger.LogInformation($"Interview score {interviewScore.Value} calculated for InteractionId: {selectedSession.Id}");
                    }
                    else
                    {
                        response = $"✅ Your {selectedType} interview for {selectedSession.JobTitle} has been submitted, but unable to calculate score. Submission time: {ConvertToLocalTime(DateTime.UtcNow):yyyy-MM-dd HH:mm:ss}.";
                        _logger.LogWarning($"Failed to calculate score for InteractionId: {selectedSession.Id}");
                    }

                    // Delete the non-selected video
                    if (!string.IsNullOrEmpty(videoPathToDelete) && System.IO.File.Exists(videoPathToDelete))
                    {
                        try
                        {
                            System.IO.File.Delete(videoPathToDelete);
                            _logger.LogInformation("Deleted video file: {VideoPath}", videoPathToDelete);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to delete video file: {VideoPath}", videoPathToDelete);
                        }
                    }

                    _chatDbService.MarkInterviewAsSubmitted(selectedSession.Id);
                    await SendPostSubmissionMessages(userId, userDetails, selectedSession.JobTitle);
                    ClearApplicationState();
                }
                else if (HttpContext.Session.GetString("FirstInterviewSessionId") is string sessionId && Regex.IsMatch(msg, @"\b(submit)\b", RegexOptions.IgnoreCase))
                {
                    var firstSession = _chatDbService.GetLatestSession(userId);
                    if (firstSession?.Id != int.Parse(sessionId))
                    {
                        response = "❌ Invalid interview session.";
                        goto EndInterviewHandling;
                    }

                    float? interviewScore = await _chatGPTService.CalculateInterviewScoreAsync(
                        firstSession.JobTitle,
                        firstSession.Questions,
                        firstSession.Answers);
                    if (interviewScore.HasValue)
                    {
                        _chatDbService.UpdateInterviewScore(firstSession.Id, interviewScore.Value);
                        response = $"✅ Your interview for {firstSession.JobTitle} has been submitted. Interview Score: {interviewScore.Value:F1}. Submission time: {ConvertToLocalTime(DateTime.UtcNow):yyyy-MM-dd HH:mm:ss}.";
                        _logger.LogInformation($"Interview score {interviewScore.Value} calculated for InteractionId: {firstSession.Id}");
                    }
                    else
                    {
                        response = $"✅ Your interview for {firstSession.JobTitle} has been submitted, but unable to calculate score. Submission time: {ConvertToLocalTime(DateTime.UtcNow):yyyy-MM-dd HH:mm:ss}.";
                        _logger.LogWarning($"Failed to calculate score for InteractionId: {firstSession.Id}");
                    }

                    _chatDbService.MarkInterviewAsSubmitted(firstSession.Id);
                    await SendPostSubmissionMessages(userId, userDetails, firstSession.JobTitle);
                    ClearApplicationState();
                }
                else if (HttpContext.Session.GetString("FirstInterviewSessionId") is string _ && Regex.IsMatch(msg, @"\b(retake)\b", RegexOptions.IgnoreCase))
                {
                    int retakeCount = HttpContext.Session.GetInt32("InterviewRetakeCount") ?? 0;
                    if (retakeCount >= 1)
                    {
                        var selectedJob = HttpContext.Session.GetString("SelectedJob") ?? "";
                        var (questions, interviewModel) = await _chatGPTService.GenerateRandomInterviewQuestionsWithModelAsync(selectedJob);
                        var newSession = new InterviewSession
                        {
                            UserId = userId,
                            JobTitle = selectedJob,
                            Questions = questions,
                            QuestionIndex = 0,
                            IsComplete = false,
                            TabSwitchCount = 0,
                            CreatedAt = DateTime.UtcNow
                        };
                        _chatDbService.SaveInterviewSession(newSession);
                        HttpContext.Session.SetString("RetakeInterviewSessionId", newSession.Id.ToString());
                        response = $"🧪 Starting retake interview for {selectedJob}.\n❓ Question 1: {questions[0]}";
                        startInterview = true;
                        modelUsed = interviewModel;
                    }
                }
                else if (HttpContext.Session.GetString("PersonalityQuestionIndex") is string indexStr && int.TryParse(indexStr, out int personalityIndex) && personalityIndex < _personalityQuestions.Count)
                {
                    if (Regex.IsMatch(msg, @"\b(when|end|finish|done)\b", RegexOptions.IgnoreCase))
                    {
                        int remainingQuestions = _personalityQuestions.Count - personalityIndex;
                        response = $"You have {remainingQuestions} personality question{(remainingQuestions != 1 ? "s" : "")} remaining.";
                    }
                    else if (_chatGPTService.IsCompanyRelated(msg))
                    {
                        response = await HandleCompanyQuery(msg, $"Continuing with the personality test. ❓ Question {personalityIndex + 1}: {_personalityQuestions[personalityIndex].Question} (Please answer 1-5, where 1=Strongly Disagree, 5=Strongly Agree)");
                    }
                    else if (int.TryParse(msg, out int answer) && answer >= 1 && answer <= 5)
                    {
                        var responses = GetPersonalityResponses();
                        responses[_personalityQuestions[personalityIndex].Id] = answer;
                        SavePersonalityResponses(responses);
                        personalityIndex++;

                        if (personalityIndex < _personalityQuestions.Count)
                        {
                            HttpContext.Session.SetString("PersonalityQuestionIndex", personalityIndex.ToString());
                            response = $"❓ Question {personalityIndex + 1}: {_personalityQuestions[personalityIndex].Question} (Please answer 1-5, where 1=Strongly Disagree, 5=Strongly Agree)";
                        }
                        else
                        {
                            var report = GeneratePersonalityReport(responses);
                            var sessionid = _chatDbService.GetLatestSession(userId) ?? new InterviewSession
                            {
                                UserId = userId,
                                JobTitle = HttpContext.Session.GetString("SelectedJob"),
                                QuestionIndex = 0,
                                Questions = new List<string>(),
                                Answers = new List<string>(),
                                CreatedAt = DateTime.UtcNow
                            };
                            _chatDbService.SaveInterviewSession(sessionid);
                            _chatDbService.SavePersonalityTestResult(userId, sessionid.Id, responses, report);

                            string reportText = "===== Personality Test Report =====\n";
                            foreach (var trait in report)
                            {
                                string interpretation = trait.Value <= 2 ? "Low" : trait.Value <= 3 ? "Average" : "High";
                                reportText += $"{trait.Key}: {interpretation} ({trait.Value:F1})\n";
                            }
                            response = $"✅ Personality test completed. Your report:\n{reportText}\nAre you ready to start the main interview? Reply 'yes' or 'no'.";
                            HttpContext.Session.Remove("PersonalityQuestionIndex");
                            HttpContext.Session.Remove("PersonalityResponses");
                            HttpContext.Session.SetString("ApplicationState", "AwaitingInterviewStart");
                            HttpContext.Session.SetString("PersonalityTestCompleted", "true");
                        }
                    }
                    else
                    {
                        response = $"Invalid response. Please answer with a number from 1 to 5 for: {_personalityQuestions[personalityIndex].Question}";
                    }
                }
                else if (HttpContext.Session.GetString("ApplicationState") is string appState)
                {
                    var currentQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == appState);
                    if (currentQuestion == null)
                    {
                        response = "❌ Invalid application state. Please start the application process again.";
                        ClearApplicationState();
                        message.BotResponse = response;
                        sessionMessages.Add(message);
                        _chatDbService.SaveMessage(message);
                        SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                        return Json(new { response, model = modelUsed, startInterview = false });
                    }

                    if (Regex.IsMatch(msg, @"\b(back|previous|go back|change)\b", RegexOptions.IgnoreCase))
                    {
                        _logger.LogInformation($"Backtracking from {appState}.");
                        var prevQuestion = _preInterviewQuestions.TakeWhile(q => q.State != appState).LastOrDefault();
                        if (prevQuestion != null)
                        {
                            HttpContext.Session.SetString("ApplicationState", prevQuestion.State);
                            response = prevQuestion.Prompt;
                        }
                        else
                        {
                            ClearApplicationState();
                            HttpContext.Session.SetString("JobList", HttpContext.Session.GetString("JobList") ?? "");
                            response = "Going back to job selection. Please reply with the job title or number you'd like to apply for, or upload your resume to find suitable jobs.";
                        }
                        HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
                    }
                    else if (_chatGPTService.IsCompanyRelated(msg) && appState != "AwaitingReasonToJoin" && appState != "AwaitingReasonForLeaving")
                    {
                        _logger.LogInformation($"Company-related intent detected during {appState}.");
                        response = await HandleCompanyQuery(msg, currentQuestion.Prompt);
                    }

                    else if (appState == "AwaitingEmail" && Regex.IsMatch(msg, @"\b(phone|mobile|number)\b", RegexOptions.IgnoreCase))
                    {
                        var contactQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == "AwaitingContact");
                        if (contactQuestion != null)
                        {
                            HttpContext.Session.SetString("ApplicationState", "AwaitingContact");
                            userDetails.Email = null;
                            HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
                            response = contactQuestion.Prompt;
                        }
                    }
                    else if (appState == "AwaitingIDProof")
                    {
                        if (Regex.IsMatch(msg, @"\b(upload|attach|id proof)\b", RegexOptions.IgnoreCase))
                        {
                            response = "Please upload your ID proof (PDF or image format) using the upload button below the input area.";
                            message.BotResponse = response;
                            sessionMessages.Add(message);
                            _chatDbService.SaveMessage(message);
                            return Json(new { response, model = modelUsed, startInterview = false });
                        }
                        else
                        {
                            response = currentQuestion.ErrorMessage;
                        }
                    }
                    else if (appState == "AwaitingInterviewStart")
                    {
                        if (Regex.IsMatch(msg, @"\b(yes|yep|yepp|yeah|sure|start)\b", RegexOptions.IgnoreCase))
                        {
                            int attemptCount = _chatDbService.GetInterviewAttemptCount(userDetails.Name, userDetails.Email, userDetails.Phone, null);
                            if (attemptCount >= 1)
                            {
                                response = "❌ You have already attempted the interview for this position. Only one interview attempt is allowed.";
                                ClearApplicationState();
                                message.BotResponse = response;
                                sessionMessages.Add(message);
                                _chatDbService.SaveMessage(message);
                                SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                                return Json(new { response, model = modelUsed, startInterview = false });
                            }

                            if (string.IsNullOrEmpty(userDetails.IDProofPath))
                            {
                                var idProofQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == "AwaitingIDProof");
                                if (idProofQuestion != null)
                                {
                                    HttpContext.Session.SetString("ApplicationState", idProofQuestion.State);
                                    response = idProofQuestion.Prompt;
                                    message.BotResponse = response;
                                    sessionMessages.Add(message);
                                    _chatDbService.SaveMessage(message);
                                    return Json(new { response, model = modelUsed, startInterview = false });
                                }
                            }

                            bool personalityTestCompleted = HttpContext.Session.GetString("PersonalityTestCompleted") == "true";
                            if (_personalityQuestions.Any() && !personalityTestCompleted)
                            {
                                HttpContext.Session.SetString("PersonalityQuestionIndex", "0");
                                response = $"Starting personality test. Please answer on a scale of 1 to 5 (1=Strongly Disagree, 5=Strongly Agree).\n❓ Question 1: {_personalityQuestions[0].Question}";
                            }
                            else
                            {
                                var selectedJob = HttpContext.Session.GetString("SelectedJob") ?? "";
                                var (questions, interviewModel) = await _chatGPTService.GenerateRandomInterviewQuestionsWithModelAsync(selectedJob);
                                var newSession = new InterviewSession
                                {
                                    UserId = userId,
                                    JobTitle = selectedJob,
                                    Questions = questions,
                                    QuestionIndex = 0,
                                    IsComplete = false,
                                    TabSwitchCount = 0,
                                    CreatedAt = DateTime.UtcNow
                                };
                                _chatDbService.SaveInterviewSession(newSession);
                                response = $"🧪 Starting interview for {selectedJob}.\n❓ Question 1: {questions[0]}";
                                startInterview = true;
                                modelUsed = interviewModel;
                            }
                        }
                        else if (Regex.IsMatch(msg, @"\b(no|nope|nah|don't)\b", RegexOptions.IgnoreCase))
                        {
                            response = "Okay, the interview will not start. How can I assist you now? To apply for another job, let me know you're interested in job openings or upload your resume.";
                            ClearApplicationState();
                            message.BotResponse = response;
                            sessionMessages.Add(message);
                            _chatDbService.SaveMessage(message);
                            SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                            return Json(new { response, model = modelUsed, startInterview = false });
                        }
                        else
                        {
                            response = currentQuestion.ErrorMessage;
                        }
                    }
                    else
                    {
                        if (currentQuestion.RequiresResume || currentQuestion.RequiresIDProof)
                        {
                            response = currentQuestion.Prompt;
                            message.BotResponse = response;
                            sessionMessages.Add(message);
                            _chatDbService.SaveMessage(message);
                            return Json(new { response, model = modelUsed, startInterview = false });
                        }
                        else if (currentQuestion.SkipAllowed && msg.ToLower() == "skip" && !string.IsNullOrEmpty(currentQuestion.SkipToState))
                        {
                            HttpContext.Session.SetString("ApplicationState", currentQuestion.SkipToState);
                            var nextQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == currentQuestion.SkipToState);
                            response = nextQuestion?.Prompt ?? "❌ Invalid skip state.";
                        }
                        else if (!Regex.IsMatch(msg, currentQuestion.ValidationRegex, RegexOptions.IgnoreCase))
                        {
                            response = currentQuestion.ErrorMessage;
                        }
                        else
                        {
                            if (appState == "AwaitingName")
                            {
                                userDetails.Name = ExtractName(msg);
                            }
                            else if (appState == "AwaitingContact")
                            {
                                userDetails.Phone = msg;
                            }
                            else if (appState == "AwaitingEmail")
                            {
                                userDetails.Email = msg;
                            }
                            else if (appState == "AwaitingEmploymentStatus")
                            {
                                userDetails.EmploymentStatus = Regex.IsMatch(msg, @"\b(yes|yep|yepp|yeah|sure)\b", RegexOptions.IgnoreCase) ? "yes" : "no";
                            }
                            else if (appState == "AwaitingExperience")
                            {
                                userDetails.Experience = msg;
                            }
                            else if (appState == "AwaitingReasonForLeaving" || appState == "AwaitingReasonToJoin")
                            {
                                userDetails.Reason = string.IsNullOrEmpty(userDetails.Reason) ? msg : userDetails.Reason + "; " + msg;
                            }

                            HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
                            if (appState == "AwaitingReasonToJoin")
                            {
                                try
                                {
                                    userDetails.CreatedAt = DateTime.UtcNow;
                                    _chatDbService.SaveUserDetails(userDetails);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to save user details in AwaitingReasonToJoin for UserId: {UserId}", userDetails.UserId);
                                    throw;
                                }
                            }

                            string nextState = currentQuestion.ConditionalNextStates?.ContainsKey(msg.ToLower()) == true
                                ? currentQuestion.ConditionalNextStates[msg.ToLower()]
                                : _preInterviewQuestions.FirstOrDefault(q => q.State != appState && _preInterviewQuestions.IndexOf(q) > _preInterviewQuestions.IndexOf(currentQuestion))?.State;
                            if (appState == "AwaitingReasonToJoin")
                            {
                                nextState = "AwaitingIDProof";
                            }
                            if (!string.IsNullOrEmpty(nextState))
                            {
                                HttpContext.Session.SetString("ApplicationState", nextState);
                                var nextQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == nextState);
                                response = nextQuestion?.Prompt ?? "❌ Invalid next state.";
                            }
                            else
                            {
                                bool personalityTestCompleted = HttpContext.Session.GetString("PersonalityTestCompleted") == "true";
                                if (_personalityQuestions.Any() && !personalityTestCompleted)
                                {
                                    HttpContext.Session.SetString("PersonalityQuestionIndex", "0");
                                    response = $"Starting personality test. Please answer on a scale of 1 to 5 (1=Strongly Disagree, 5=Strongly Agree).\n❓ Question 1: {_personalityQuestions[0].Question}";
                                }
                                else
                                {
                                    var selectedJob = HttpContext.Session.GetString("SelectedJob") ?? "";
                                    var (questions, interviewModel) = await _chatGPTService.GenerateRandomInterviewQuestionsWithModelAsync(selectedJob);
                                    var newSession = new InterviewSession
                                    {
                                        UserId = userId,
                                        JobTitle = selectedJob,
                                        Questions = questions,
                                        QuestionIndex = 0,
                                        IsComplete = false,
                                        TabSwitchCount = 0,
                                        CreatedAt = DateTime.UtcNow
                                    };
                                    _chatDbService.SaveInterviewSession(newSession);
                                    response = $"🧪 Starting interview for {selectedJob}.\n❓ Question 1: {questions[0]}";
                                    startInterview = true;
                                    modelUsed = interviewModel;
                                }
                            }
                        }
                    }
                    HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
                }
                else if (HttpContext.Session.GetString("JobList") is string jobListStr && !string.IsNullOrWhiteSpace(jobListStr))
                {
                    var jobList = jobListStr.Split("||").ToList();
                    string selectedJob = "";

                    if (int.TryParse(msg, out int selectedIndex) && selectedIndex >= 1 && selectedIndex <= jobList.Count)
                    {
                        selectedJob = jobList[selectedIndex - 1];
                    }
                    else
                    {
                        selectedJob = jobList.FirstOrDefault(j => msg.ToLower().Contains(j.ToLower())) ?? "";
                    }

                    if (!string.IsNullOrEmpty(selectedJob))
                    {
                        HttpContext.Session.SetString("SelectedJob", selectedJob);
                        HttpContext.Session.SetString("ApplicationState", _preInterviewQuestions.FirstOrDefault()?.State ?? "AwaitingName");
                        HttpContext.Session.Remove("JobList");
                        response = $"You’ve selected {selectedJob}. {_preInterviewQuestions.FirstOrDefault()?.Prompt}";
                    }
                    else
                    {
                        response = await HandleCompanyQuery(msg, "Please reply with the job title or number you'd like to apply for, or upload your resume to find suitable jobs.");
                    }
                }
                else if (await _chatGPTService.IsJobIntentAsync(msg))
                {
                    _logger.LogInformation("Job intent detected for query: {msg}. Fetching job openings.", msg);
                    var (jobList, jobModel) = await _chatGPTService.GetJobOpeningsAsync();
                    modelUsed = jobModel;

                    if (jobList.Count > 0)
                    {
                        response = "🧑‍💻 Current job openings:\n";
                        for (int i = 0; i < jobList.Count; i++)
                            response += $"{i + 1}. {jobList[i]}\n";

                        response += "\nPlease reply with the job title or number you'd like to apply for, or upload your resume to find suitable jobs.";
                        HttpContext.Session.SetString("JobList", string.Join("||", jobList));
                        _logger.LogInformation("Job list generated: {jobList}", string.Join(", ", jobList));
                    }
                    else
                    {
                        response = "❌ Sorry, no job openings found at the moment. You can upload your resume to check for matching jobs.";
                        _logger.LogWarning("No job openings available or API returned empty list.");
                    }
                }
                else if (await _chatGPTService.IsLocationIntentAsync(msg) || _chatGPTService.IsLocationRelated(msg))
                {
                    _logger.LogInformation("Location-related intent detected for query: {msg}.", msg);
                    var (resp, model) = await _chatGPTService.GetSmartResponseAsync(msg);
                    modelUsed = model;
                    response = resp;
                }
                else if (_chatGPTService.IsCompanyRelated(msg))
                {
                    _logger.LogInformation("Company-related intent detected for query: {msg}.", msg);
                    HttpContext.Session.Remove("JobList");
                    HttpContext.Session.Remove("ApplicationState");
                    response = await HandleCompanyQuery(msg);
                }
                else
                {
                    _logger.LogInformation($"No specific intent detected for query: {msg}. Attempting to process with GetSmartResponseAsync.");
                    var (resp, model) = await _chatGPTService.GetSmartResponseAsync(msg);
                    modelUsed = model;
                    response = resp;
                }

            EndInterviewHandling:
                message.BotResponse = response;
                message.Model = modelUsed;
                sessionMessages.Add(message);
                HttpContext.Session.SetString("SessionMessages", JsonConvert.SerializeObject(sessionMessages));
                _chatDbService.SaveMessage(message);
                SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);

                return Json(new { response, model = modelUsed, startInterview });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing query: {msg} in state: {HttpContext.Session.GetString("ApplicationState")}");
                response = "❌ Unexpected error occurred. Please try again or contact us.";
                modelUsed = "error";
                message.BotResponse = response;
                sessionMessages.Add(message);
                HttpContext.Session.SetString("SessionMessages", JsonConvert.SerializeObject(sessionMessages));
                _chatDbService.SaveMessage(message);
                return Json(new { response, model = modelUsed, startInterview = false });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UploadResume(IFormFile resume)
        {
            string userId = HttpContext.Session.Id;
            string response = "";
            string modelUsed = "custom";

            EnsureUserRecord(userId);

            try
            {
                if (resume == null || resume.Length == 0)
                {
                    response = "❌ No file uploaded. Please upload a PDF or Word document.";
                    return Json(new { response, model = modelUsed });
                }

                if (resume.Length > 5 * 1024 * 1024)
                {
                    response = "❌ File size exceeds 5MB. Please upload a smaller file.";
                    return Json(new { response, model = modelUsed });
                }

                var extension = Path.GetExtension(resume.FileName).ToLower();
                if (extension != ".pdf" && extension != ".docx")
                {
                    response = "❌ Invalid file format. Please upload a PDF or Word (.docx) document.";
                    return Json(new { response, model = modelUsed });
                }

                string resumeText;
                using (var stream = new MemoryStream())
                {
                    await resume.CopyToAsync(stream);
                    stream.Position = 0;
                    if (extension == ".pdf")
                    {
                        using (var reader = new PdfReader(stream))
                        using (var pdfDoc = new PdfDocument(reader))
                        {
                            var text = new StringBuilder();
                            for (int i = 1; i <= pdfDoc.GetNumberOfPages(); i++)
                            {
                                text.Append(PdfTextExtractor.GetTextFromPage(pdfDoc.GetPage(i)));
                            }
                            resumeText = text.ToString();
                        }
                    }
                    else
                    {
                        using (var doc = DocX.Load(stream))
                        {
                            resumeText = doc.Text;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(resumeText))
                {
                    response = "❌ Unable to extract content from the resume. Please ensure the file contains readable text.";
                    return Json(new { response, model = modelUsed });
                }

                var userDetailsStr = HttpContext.Session.GetString("UserDetails");
                var userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : new UserDetails { UserId = userId };
                var appState = HttpContext.Session.GetString("ApplicationState");

                var userFolderName = string.IsNullOrEmpty(userDetails.Name) ? userId : userDetails.Name.Replace(" ", "_");
                var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), _resumeFolder, userFolderName);
                if (!Directory.Exists(uploadsFolder))
                {
                    Directory.CreateDirectory(uploadsFolder);
                }

                var fileName = $"{userFolderName}_{Guid.NewGuid()}{extension}";
                var filePath = Path.Combine(uploadsFolder, fileName);
                using (var fileStream = new FileStream(filePath, FileMode.Create))
                {
                    await resume.CopyToAsync(fileStream);
                }

                userDetails.ResumePath = filePath;
                userDetails.ResumeType = extension == ".pdf" ? "PDF Document" : "Word Document";

                int? resumeScore = null;
                var selectedJob = HttpContext.Session.GetString("SelectedJob");
                if (!string.IsNullOrEmpty(selectedJob) || appState == "AwaitingResume")
                {
                    resumeScore = await _chatGPTService.CalculateResumeScoreAsync(resumeText, selectedJob ?? "");
                    userDetails.ResumeScore = resumeScore;
                }

                userDetails.CreatedAt = DateTime.UtcNow;
                HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
                _chatDbService.SaveUserDetails(userDetails);

                if (appState == "AwaitingResume")
                {
                    var nextQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == "AwaitingContact");
                    if (nextQuestion != null)
                    {
                        HttpContext.Session.SetString("ApplicationState", nextQuestion.State);
                        response = $"Resume uploaded and saved: {fileName}\nResume Score: {resumeScore ?? 0}\n\n{nextQuestion.Prompt}";
                    }
                    else
                    {
                        response = "❌ Invalid application state after resume upload. Please start the application process again.";
                        HttpContext.Session.Remove("ApplicationState");
                        HttpContext.Session.Remove("SelectedJob");
                    }
                }
                else
                {
                    HttpContext.Session.SetString("ResumeContent", resumeText);
                    var (matchingJobs, jobModel) = await _chatGPTService.FindJobsByResumeAsync(resumeText);
                    modelUsed = jobModel;

                    if (matchingJobs.Count > 0)
                    {
                        response = "🧑‍💻 Based on your resume, here are the matching job openings:\n";
                        for (int i = 0; i < matchingJobs.Count; i++)
                            response += $"{i + 1}. {matchingJobs[i]}\n";
                        response += "\nPlease reply with the job title or number you'd like to apply for.";
                        HttpContext.Session.SetString("JobList", string.Join("||", matchingJobs));
                    }
                    else
                    {
                        response = "❌ No matching job openings found for your resume. You can try asking about other job opportunities or contact us for more information.";
                    }
                }

                var message = new ChatMessage
                {
                    UserId = userId,
                    UserMessage = "Uploaded resume",
                    BotResponse = response,
                    Model = modelUsed,
                    CreatedAt = DateTime.UtcNow
                };

                var sessionMessages = HttpContext.Session.GetString("SessionMessages") is string messagesStr
                    ? JsonConvert.DeserializeObject<List<ChatMessage>>(messagesStr) ?? new List<ChatMessage>()
                    : new List<ChatMessage>();

                sessionMessages.Add(message);
                _chatDbService.SaveMessage(message);
                HttpContext.Session.SetString("SessionMessages", JsonConvert.SerializeObject(sessionMessages));

                return Json(new { response, model = modelUsed });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing resume upload.");
                response = "❌ Error processing your resume. Please try again or contact us.";
                return Json(new { response, model = "error" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> SubmitInterview(int interactionId)
        {
            string userId = HttpContext.Session.Id;
            string response = "";
            string modelUsed = "custom";

            try
            {
                var session = _chatDbService.GetLatestSession(userId);
                if (session == null || session.Id != interactionId || session.IsSubmitted)
                {
                    response = "❌ Invalid or already submitted interview session.";
                    return Json(new { success = false, response, model = modelUsed });
                }

                session.IsSubmitted = true;
                session.IsComplete = true;
                _chatDbService.SaveInterviewSession(session);

                float? interviewScore = await _chatGPTService.CalculateInterviewScoreAsync(
                    session.JobTitle,
                    session.Questions,
                    session.Answers
                );

                if (interviewScore.HasValue)
                {
                    _chatDbService.UpdateInterviewScore(interactionId, interviewScore.Value);
                    response = $"✅ Interview submitted successfully. Interview Score: {interviewScore.Value:F1}. Submission time: {ConvertToLocalTime(DateTime.UtcNow):yyyy-MM-dd HH:mm:ss}.";
                    _logger.LogInformation($"Interview score {interviewScore.Value} calculated and saved for InteractionId: {interactionId}");
                }
                else
                {
                    response = $"✅ Interview submitted successfully, but unable to calculate interview score. Submission time: {ConvertToLocalTime(DateTime.UtcNow):yyyy-MM-dd HH:mm:ss}.";
                    _logger.LogWarning($"Failed to calculate interview score for InteractionId: {interactionId}");
                }

                var message = new ChatMessage
                {
                    UserId = userId,
                    UserMessage = "Submitted interview",
                    BotResponse = response,
                    Model = modelUsed,
                    CreatedAt = DateTime.UtcNow
                };

                var sessionMessages = HttpContext.Session.GetString("SessionMessages") is string messagesStr
                    ? JsonConvert.DeserializeObject<List<ChatMessage>>(messagesStr) ?? new List<ChatMessage>()
                    : new List<ChatMessage>();

                sessionMessages.Add(message);
                _chatDbService.SaveMessage(message);
                HttpContext.Session.SetString("SessionMessages", JsonConvert.SerializeObject(sessionMessages));

                return Json(new { success = true, response, model = modelUsed });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error submitting interview for InteractionId: {InteractionId}", interactionId);
                response = "❌ Error submitting interview. Please try again or contact us.";
                return Json(new { success = false, response, model = "error" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UploadIDProof()
        {
            var userId = HttpContext.Session.Id;
            var userDetailsStr = HttpContext.Session.GetString("UserDetails");
            var userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : new UserDetails { UserId = userId };
            var chatMessage = new ChatMessage
            {
                UserId = userId,
                UserMessage = "Uploaded ID proof",
                Model = "custom",
                CreatedAt = DateTime.UtcNow
            };

            string response = "";
            EnsureUserRecord(userId);

            try
            {
                var file = Request.Form.Files["idProof"];
                if (file == null || file.Length == 0)
                {
                    response = "No ID proof uploaded. Please upload a JPG, PNG, or PDF of your government-issued ID (e.g., passport, driver's license).";
                    chatMessage.BotResponse = response;
                    _chatDbService.SaveMessage(chatMessage);
                    return Json(new { success = false, message = response, reason = "no_file", model = "custom", startInterview = false });
                }

                if (file.Length > 5 * 1024 * 1024)
                {
                    response = "File size exceeds 5MB. Please upload a smaller file.";
                    chatMessage.BotResponse = response;
                    _chatDbService.SaveMessage(chatMessage);
                    return Json(new { success = false, message = response, reason = "file_too_large", model = "custom", startInterview = false });
                }

                var extension = Path.GetExtension(file.FileName).ToLower();
                if (extension != ".jpg" && extension != ".jpeg" && extension != ".png" && extension != ".pdf")
                {
                    response = "Invalid file format. Please upload a JPG, PNG, or PDF file.";
                    chatMessage.BotResponse = response;
                    _chatDbService.SaveMessage(chatMessage);
                    return Json(new { success = false, message = response, reason = "invalid_format", model = "custom", startInterview = false });
                }

                int retryCount = HttpContext.Session.GetInt32("IDProofRetryCount") ?? 0;
                if (retryCount >= 3)
                {
                    response = "You have exceeded the maximum number of ID proof upload attempts (3). Please contact support or start the application process again.";
                    chatMessage.BotResponse = response;
                    _chatDbService.SaveMessage(chatMessage);
                    HttpContext.Session.Remove("ApplicationState");
                    HttpContext.Session.Remove("SelectedJob");
                    return Json(new { success = false, message = response, reason = "retry_limit_exceeded", model = "custom", startInterview = false });
                }

                int attemptCount = _chatDbService.GetInterviewAttemptCount(userDetails.Name, userDetails.Email, userDetails.Phone, null);
                if (attemptCount >= 1)
                {
                    response = "❌ You have already attempted the interview for this position. Only one interview attempt is allowed.";
                    chatMessage.BotResponse = response;
                    _chatDbService.SaveMessage(chatMessage);
                    HttpContext.Session.Remove("ApplicationState");
                    HttpContext.Session.Remove("SelectedJob");
                    return Json(new { success = false, message = response, reason = "interview_limit_exceeded", model = "custom", startInterview = false });
                }

                var userFolderName = string.IsNullOrEmpty(userDetails.Name) ? userId : userDetails.Name.Replace(" ", "_");
                var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), _idProofFolder, userFolderName);
                if (!Directory.Exists(uploadsFolder))
                {
                    Directory.CreateDirectory(uploadsFolder);
                }

                var fileName = $"{userFolderName}_{Guid.NewGuid()}{extension}";
                var filePath = Path.Combine(uploadsFolder, fileName);
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                userDetails.IDProofPath = filePath;
                userDetails.IDProofType = extension == ".pdf" ? "PDF Document" : "Image";
                userDetails.CreatedAt = DateTime.UtcNow;
                HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
                _chatDbService.SaveUserDetails(userDetails);
                HttpContext.Session.SetInt32("IDProofRetryCount", 0);

                response = $"ID proof uploaded and saved: {fileName}. Upload time: {ConvertToLocalTime(DateTime.UtcNow):yyyy-MM-dd HH:mm:ss}";
                chatMessage.BotResponse = response;
                _chatDbService.SaveMessage(chatMessage);

                var selectedJob = HttpContext.Session.GetString("SelectedJob") ?? "";
                if (!string.IsNullOrEmpty(selectedJob))
                {
                    var nextQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == "AwaitingInterviewStart");
                    if (nextQuestion != null)
                    {
                        HttpContext.Session.SetString("ApplicationState", nextQuestion.State);
                        response = nextQuestion.Prompt;
                        chatMessage.BotResponse = response;
                        _chatDbService.SaveMessage(chatMessage);
                        return Json(new { success = true, response, model = "custom", startInterview = false });
                    }
                    else
                    {
                        response = "❌ Invalid application state after ID upload. Please start the application process again.";
                        chatMessage.BotResponse = response;
                        _chatDbService.SaveMessage(chatMessage);
                        return Json(new { success = false, message = response, reason = "invalid_state", model = "custom", startInterview = false });
                    }
                }
                else
                {
                    response = "❌ No job selected. Please start the application process again or upload your resume to find suitable jobs.";
                    chatMessage.BotResponse = response;
                    _chatDbService.SaveMessage(chatMessage);
                    return Json(new { success = false, message = response, reason = "no_job_selected", model = "custom", startInterview = false });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading ID proof.");
                response = "Error uploading ID proof. Please try again or contact support.";
                chatMessage.BotResponse = response;
                _chatDbService.SaveMessage(chatMessage);
                return Json(new { success = false, message = response, reason = "exception", model = "error", startInterview = false });
            }
        }

        //[HttpPost]
        //public async Task<IActionResult> UploadInterviewVideo()
        //{
        //    try
        //    {
        //        var file = Request.Form.Files["video"];
        //        if (file == null || file.Length == 0)
        //        {
        //            return BadRequest("No video file uploaded.");
        //        }

        //        var userId = HttpContext.Session.Id;
        //        var userDetailsStr = HttpContext.Session.GetString("UserDetails");
        //        var userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : new UserDetails { UserId = userId };

        //        var userFolderName = string.IsNullOrEmpty(userDetails.Name) ? userId : userDetails.Name.Replace(" ", "_");
        //        var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), _interviewVideoFolder, userFolderName);
        //        if (!Directory.Exists(uploadsFolder))
        //        {
        //            Directory.CreateDirectory(uploadsFolder);
        //        }

        //        var fileName = $"{userFolderName}_{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
        //        var filePath = Path.Combine(uploadsFolder, fileName);
        //        using (var stream = new FileStream(filePath, FileMode.Create))
        //        {
        //            await file.CopyToAsync(stream);
        //        }

        //        var session = _chatDbService.GetLatestSession(userId);
        //        if (session != null)
        //        {
        //            session.VideoPath = filePath;
        //            session.CreatedAt = DateTime.UtcNow;
        //            _chatDbService.UpdateInterviewSession(session);
        //        }

        //        var chatMessage = new ChatMessage
        //        {
        //            UserId = userId,
        //            BotResponse = $"Video recorded and saved: {fileName}. Upload time: {ConvertToLocalTime(DateTime.UtcNow):yyyy-MM-dd HH:mm:ss}",
        //            CreatedAt = DateTime.UtcNow,
        //            Model = "custom"
        //        };
        //        _chatDbService.SaveMessage(chatMessage);

        //        return Json(new { fileName });
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error uploading interview video");
        //        return StatusCode(500, "Error uploading video");
        //    }
        //}

        [HttpPost]
        public async Task<IActionResult> UploadInterviewVideo()
        {
            try
            {
                var file = Request.Form.Files["video"];
                if (file == null || file.Length == 0)
                {
                    return BadRequest("No video file uploaded.");
                }

                var userId = HttpContext.Session.Id;
                var userDetailsStr = HttpContext.Session.GetString("UserDetails");
                var userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : new UserDetails { UserId = userId };

                var userFolderName = string.IsNullOrEmpty(userDetails.Name) ? userId : userDetails.Name.Replace(" ", "_");
                var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), _interviewVideoFolder, userFolderName);
                if (!Directory.Exists(uploadsFolder))
                {
                    Directory.CreateDirectory(uploadsFolder);
                }

                var fileName = $"{userFolderName}_{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
                var filePath = Path.Combine(uploadsFolder, fileName);
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                var session = _chatDbService.GetLatestSession(userId);
                if (session != null)
                {
                    session.VideoPath = filePath;
                    _chatDbService.SaveInterviewSession(session);
                }
                else
                {
                    _logger.LogWarning("No active session found for UserId: {UserId}", userId);
                    return Json(new { success = false, message = "No active interview session found to save video." });
                }

                var chatMessage = new ChatMessage
                {
                    UserId = userId,
                    BotResponse = $"Video recorded and saved: {fileName}. Upload time: {ConvertToLocalTime(DateTime.UtcNow):yyyy-MM-dd HH:mm:ss}",
                    CreatedAt = DateTime.UtcNow,
                    Model = "custom"
                };
                _chatDbService.SaveMessage(chatMessage);

                return Json(new { fileName, success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading interview video for UserId: {UserId}", HttpContext.Session.Id);
                return StatusCode(500, new { success = false, message = "Error uploading video. Please try again." });
            }
        }

        [HttpGet]
        public IActionResult ViewInterviewVideo(string fileName)
        {
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), _interviewVideoFolder, fileName);
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound("Video not found.");
            }

            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            return File(stream, "video/webm");
        }

        [HttpGet]
        public IActionResult ViewIDProof(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                _logger.LogWarning("ViewIDProof called with null or empty fileName");
                return NotFound("ID proof not found.");
            }

            // Use the provided path directly if it’s absolute, else construct it
            var filePath = Path.IsPathRooted(fileName) ? fileName : Path.Combine(Directory.GetCurrentDirectory(), _idProofFolder, fileName);
            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("ID proof not found at path: {FilePath}", filePath);
                return NotFound("ID proof not found.");
            }

            try
            {
                _logger.LogInformation("Serving ID proof from: {FilePath}", filePath);
                var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                var extension = Path.GetExtension(filePath).ToLower();
                string contentType = extension switch
                {
                    ".jpg" => "image/jpeg",
                    ".jpeg" => "image/jpeg",
                    ".png" => "image/png",
                    ".pdf" => "application/pdf",
                    _ => "application/octet-stream"
                };
                return File(stream, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accessing ID proof file at path: {FilePath}", filePath);
                return NotFound("Error accessing ID proof file.");
            }
        }

        [HttpGet]
        public IActionResult GetInterviewStatus()
        {
            try
            {
                string userId = HttpContext.Session.Id;
                EnsureUserRecord(userId);

                var session = _chatDbService.GetLatestSession(userId);
                if (session != null && !session.IsComplete && session.QuestionIndex < session.Questions.Count)
                {
                    // Interview is active and has pending questions
                    string currentQuestion = $"🧪 Continuing interview for {session.JobTitle}.\n❓ Question {session.QuestionIndex + 1}: {session.Questions[session.QuestionIndex]}";
                    return Json(new { isInterviewActive = true, currentQuestion });
                }
                else if (HttpContext.Session.GetString("FirstInterviewSessionId") is string firstSessionId)
                {
                    // Interview is complete, but user needs to choose between first and retake
                    string currentQuestion = "✅ Thank you for completing the interview. Please choose which interview to submit: reply 'first' to submit your first attempt or 'retake' to submit your retake.";
                    return Json(new { isInterviewActive = true, currentQuestion });
                }
                else
                {
                    // No active interview
                    return Json(new { isInterviewActive = false });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving interview status for UserId: {UserId}", HttpContext.Session.Id);
                return Json(new { isInterviewActive = false, message = "Error retrieving interview status." });
            }
        }

        //private async Task SendPostSubmissionMessages(string userId, UserDetails userDetails, string jobTitle)
        //{
        //    try
        //    {
        //        var templates = _chatDbService.GetMessageTemplates();
        //        var emailTemplate = templates.FirstOrDefault(t => t.MessageType == "Email" && t.IsDefault);
        //        var whatsappTemplate = templates.FirstOrDefault(t => t.MessageType == "WhatsApp" && t.IsDefault);

        //        using var conn = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        //        conn.Open();
        //        var cmd = new SqlCommand("SELECT EmailSent, SMSSent FROM Users WHERE UserId = @UserId", conn);
        //        cmd.Parameters.AddWithValue("@UserId", userId);
        //        using var reader = cmd.ExecuteReader();
        //        bool emailSent = false, whatsappSent = false;
        //        if (reader.Read())
        //        {
        //            emailSent = (bool)reader["EmailSent"];
        //            whatsappSent = (bool)reader["SMSSent"];
        //        }
        //        reader.Close();

        //        if (emailTemplate != null && !string.IsNullOrEmpty(userDetails?.Email) && !emailSent)
        //        {
        //            var body = emailTemplate.TemplateContent
        //                .Replace("{Name}", userDetails.Name ?? "Candidate")
        //                .Replace("{JobTitle}", jobTitle ?? "the position");
        //            await _notificationService.SendEmailAsync(userDetails.Email, "Interview Submission Confirmation", body);
        //            _chatDbService.UpdateUserMessageStatus(userId, true, whatsappSent);
        //        }

        //        if (whatsappTemplate != null && !string.IsNullOrEmpty(userDetails?.Phone) && !whatsappSent)
        //        {
        //            var body = whatsappTemplate.TemplateContent
        //                .Replace("{Name}", userDetails.Name ?? "Candidate")
        //                .Replace("{JobTitle}", jobTitle ?? "the position");
        //            await _notificationService.SendWhatsAppAsync(userDetails.Phone, body);
        //            _chatDbService.UpdateUserMessageStatus(userId, emailSent, true);
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error sending post-submission messages for UserId: {UserId}", userId);
        //    }
        //}

        private async Task SendPostSubmissionMessages(string userId, UserDetails userDetails, string jobTitle)
        {
            try
            {
                var templates = _chatDbService.GetMessageTemplates();
                var emailTemplate = templates.FirstOrDefault(t => t.MessageType == "Email" && t.IsDefault);
                var whatsappTemplate = templates.FirstOrDefault(t => t.MessageType == "WhatsApp" && t.IsDefault);

                using var conn = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
                conn.Open();
                var cmd = new SqlCommand("SELECT EmailSent, WhatsAppSent FROM Users WHERE UserId = @UserId", conn);
                cmd.Parameters.AddWithValue("@UserId", userId);
                using var reader = cmd.ExecuteReader();
                bool emailSent = false, whatsappSent = false;
                if (reader.Read())
                {
                    emailSent = reader["EmailSent"] != DBNull.Value && (bool)reader["EmailSent"];
                    whatsappSent = reader["WhatsAppSent"] != DBNull.Value && (bool)reader["WhatsAppSent"];
                }
                reader.Close();

                if (emailTemplate != null && !string.IsNullOrEmpty(userDetails?.Email) && !emailSent)
                {
                    var body = emailTemplate.TemplateContent
                        .Replace("{Name}", userDetails.Name ?? "Candidate")
                        .Replace("{JobTitle}", jobTitle ?? "the position");
                    await _notificationService.SendEmailAsync(userDetails.Email, "Interview Submission Confirmation", body);
                    emailSent = true;
                }

                if (whatsappTemplate != null && !string.IsNullOrEmpty(userDetails?.Phone) && !whatsappSent)
                {
                    var body = whatsappTemplate.TemplateContent
                        .Replace("{Name}", userDetails.Name ?? "Candidate")
                        .Replace("{JobTitle}", jobTitle ?? "the position");
                    await _notificationService.SendWhatsAppAsync(userDetails.Phone, body);
                    whatsappSent = true;
                }

                _chatDbService.UpdateUserMessageStatus(userId, emailSent, whatsappSent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending post-submission messages for UserId: {UserId}", userId);
            }
        }

        [HttpPost]
        public IActionResult IncrementTabSwitchCount()
        {
            try
            {
                string userId = HttpContext.Session.Id;
                var session = _chatDbService.GetLatestSession(userId);
                if (session == null || session.IsComplete || session.Questions == null || !session.Questions.Any())
                {
                    _logger.LogWarning("No active interview session found for UserId: {UserId}", userId);
                    return Json(new { success = false, message = "No active interview session found." });
                }

                _chatDbService.UpdateTabSwitchCount(userId, 1); // Increment by 1
                _logger.LogInformation("Tab switch count incremented for UserId: {UserId}, InteractionId: {InteractionId}", userId, session.Id);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error incrementing tab switch count for UserId: {UserId}", HttpContext.Session.Id);
                return Json(new { success = false, message = "Error recording tab switch." });
            }
        }

        [HttpGet]
        public IActionResult GetTabSwitchCount()
        {
            try
            {
                string userId = HttpContext.Session.Id;
                var session = _chatDbService.GetLatestSession(userId);
                if (session == null || session.Questions == null || !session.Questions.Any())
                {
                    return Json(new { success = false, count = 0, message = "No active interview session found." });
                }

                return Json(new { success = true, count = session.TabSwitchCount });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving tab switch count for UserId: {UserId}", HttpContext.Session.Id);
                return Json(new { success = false, count = 0, message = "Error retrieving tab switch count." });
            }
        }

        [HttpPost]
        public IActionResult RecordCopyPasteEvent([FromBody] CopyPasteEventModel model)
        {
            try
            {
                string userId = HttpContext.Session.Id;
                var session = _chatDbService.GetLatestSession(userId);
                if (session == null || session.IsComplete || session.Questions == null || !session.Questions.Any())
                {
                    _logger.LogWarning("No active interview session found for UserId: {UserId}", userId);
                    return Json(new { success = false, message = "No active interview session found." });
                }

                _chatDbService.SaveCopyPasteEvent(new CopyPasteEvent
                {
                    UserId = userId,
                    InteractionId = session.Id,
                    Type = model.Type,
                    Content = model.Content?.Length > 1000 ? model.Content.Substring(0, 1000) : model.Content, // Truncate to avoid DB size limits
                    Timestamp = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                });

                _logger.LogInformation("Copy-paste event ({Type}) recorded for UserId: {UserId}, InteractionId: {InteractionId}", model.Type, userId, session.Id);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recording copy-paste event for UserId: {UserId}", HttpContext.Session.Id);
                return Json(new { success = false, message = "Error recording copy-paste event." });
            }
        }

        //[HttpPost]
        //public IActionResult RecordScreenshotEvent()
        //{
        //    try
        //    {
        //        string userId = HttpContext.Session.Id;
        //        var session = _chatDbService.GetLatestSession(userId);
        //        if (session == null || session.IsComplete || session.Questions == null || !session.Questions.Any())
        //        {
        //            _logger.LogWarning("No active interview session found for UserId: {UserId}", userId);
        //            return Json(new { success = false, message = "No active interview session found." });
        //        }

        //        _chatDbService.SaveScreenshotEvent(new ScreenshotEvent
        //        {
        //            UserId = userId,
        //            InteractionId = session.Id,
        //            Timestamp = DateTime.UtcNow,
        //            CreatedAt = DateTime.UtcNow
        //        });

        //        _logger.LogInformation("Screenshot event recorded for UserId: {UserId}, InteractionId: {InteractionId}", userId, session.Id);
        //        return Json(new { success = true });
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error recording screenshot event for UserId: {UserId}", HttpContext.Session.Id);
        //        return Json(new { success = false, message = "Error recording screenshot event." });
        //    }
        //}

        [HttpPost]
        public IActionResult RecordTabSwitchEvent([FromBody] TabSwitchEventModel model)
        {
            try
            {
                string userId = HttpContext.Session.Id;
                var session = _chatDbService.GetLatestSession(userId);
                if (session == null || session.IsComplete || session.Questions == null || !session.Questions.Any())
                {
                    _logger.LogWarning("No active interview session found for UserId: {UserId}", userId);
                    return Json(new { success = false, message = "No active interview session found." });
                }

                _chatDbService.SaveTabSwitchEvent(new TabSwitchEvent
                {
                    UserId = userId,
                    InteractionId = session.Id,
                    Timestamp = DateTime.UtcNow,
                    Duration = model.Duration,
                    CreatedAt = DateTime.UtcNow
                });

                _logger.LogInformation("Tab switch event recorded with duration {Duration}s for UserId: {UserId}, InteractionId: {InteractionId}", model.Duration, userId, session.Id);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recording tab switch event for UserId: {UserId}", HttpContext.Session.Id);
                return Json(new { success = false, message = "Error recording tab switch event." });
            }
        }
    }

    public class CopyPasteEventModel
    {
        public string Type { get; set; } // "copy" or "paste"
        public string Content { get; set; }
    }

    public class TabSwitchEventModel
    {
        public double Duration { get; set; } // Duration in seconds
    }

    // Models for the new event types
    public class CopyPasteEvent
    {
        public int EventId { get; set; }
        public string UserId { get; set; }
        public int InteractionId { get; set; }
        public string Type { get; set; } // "copy" or "paste"
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    //public class ScreenshotEvent
    //{
    //    public int EventId { get; set; }
    //    public string UserId { get; set; }
    //    public int InteractionId { get; set; }
    //    public DateTime Timestamp { get; set; }
    //    public DateTime CreatedAt { get; set; }
    //}

    public class TabSwitchEvent
    {
        public int EventId { get; set; }
        public string UserId { get; set; }
        public int InteractionId { get; set; }
        public DateTime Timestamp { get; set; }
        public double Duration { get; set; } // Duration in seconds
        public DateTime CreatedAt { get; set; }
    }
}