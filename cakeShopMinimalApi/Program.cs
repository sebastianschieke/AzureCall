using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Mvc;
using OpenAI.Chat;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using Azure.Messaging.EventGrid.SystemEvents;
using Azure.Messaging.EventGrid;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables()
    .Build()
    ?? throw new InvalidOperationException("Configuration is not provided.");

var aiDeploymentName = config["OPENAI_DEPLOYMENT_NAME"] ?? throw new InvalidOperationException("OPENAI_DEPLOYMENT_NAME is not provided.");
var openAIAPIKey = config["OPENAI_KEY"] ?? throw new InvalidOperationException("OPENAI_KEY is not provided.");
var openAIUrl = config["OPENAI_ENDPOINT"] ?? throw new InvalidOperationException("OPENAI_ENDPOINT is not provided.");

var aiSearchKey = config["AI_SEARCH_KEY"] ?? throw new InvalidOperationException("AI_SEARCH_KEY is not provided.");
var aiSearchEndpoint = config["AI_SEARCH_ENDPOINT"] ?? throw new InvalidOperationException("AI_SEARCH_ENDPOINT is not provided.");
var aiSearchIndex = config["AI_SEARCH_INDEXNAME"] ?? throw new InvalidOperationException("AI_SEARCH_INDEXNAME is not provided.");

var acsConnectionString = config["ACS_CONNECTION_STRING"] ?? throw new InvalidOperationException("ACS_CONNECTION_STRING is not provided.");
var cognitiveServicesEndpoint = config["AZURE_COG_SERVICES_ENDPOINT"] ?? throw new InvalidOperationException("AZURE_COG_SERVICES_ENDPOINT");
var callbackUriHostString = config["CALLBACK_URI"] ?? throw new InvalidOperationException("CALLBACK_URI is not provided.");

// Add Azure Blob Storage connection string
var blobStorageConnectionString = config["BLOB_STORAGE_CONNECTION_STRING"] ?? throw new InvalidOperationException("BLOB_STORAGE_CONNECTION_STRING is not provided.");

PhoneNumberIdentifier caller = new(config["ACS_PHONE_NUMBER"] ?? throw new InvalidOperationException("ACS_PHONE_NUMBER is not provided."));
// Changed voice to German female voice
var speechVoiceName = "de-DE-ConradNeural";

// Get Azure Open AI chat client
AzureOpenAIClient _aiClient = new AzureOpenAIClient(
    new Uri(openAIUrl),
    new System.ClientModel.ApiKeyCredential(openAIAPIKey)
);

ChatClient chatClient = _aiClient.GetChatClient(aiDeploymentName);

// Setting up the AI search index that has the knowledge base for chat completion API
ChatCompletionOptions options = new();
options.AddDataSource(new AzureSearchChatDataSource()
{
    Endpoint = new Uri(aiSearchEndpoint),
    IndexName = aiSearchIndex,
    Authentication = DataSourceAuthentication.FromApiKey(aiSearchKey)
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Dictionary to store conversation transcripts and states
ConcurrentDictionary<string, StringBuilder> transcriptCache = new();
ConcurrentDictionary<string, List<ChatMessage>> chatHistoryCache = new();
ConcurrentDictionary<string, string> conversationState = new(); // Track conversation state
var callAutomationClient = new CallAutomationClient(acsConnectionString);

// Sentence end symbols for splitting the response into sentences.
List<string> sentenceSeparators = new() { ".", "!", "?", ";", "。", "！", "？", "；", "\n" };

// Handle incoming call
app.MapPost("/api/event", async ([FromBody] EventGridEvent[] eventGridEvents) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        // Handle system events
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
            else if (eventGridEvent.EventType == "Microsoft.Communication.IncomingCall")
            {
                if (eventData is AcsIncomingCallEventData incomingCallEventData)
                {
                    var encodedCallerId = WebUtility.UrlEncode(incomingCallEventData.FromCommunicationIdentifier.PhoneNumber.Value);
                    var contextId = Guid.NewGuid().ToString();
                    var callbackUri = new Uri(new Uri(callbackUriHostString), $"/api/callbacks/{contextId}?callerId={encodedCallerId}");
                    var options = new AnswerCallOptions(incomingCallEventData.IncomingCallContext, callbackUri)
                    {
                        CallIntelligenceOptions = new CallIntelligenceOptions() { CognitiveServicesEndpoint = new Uri(cognitiveServicesEndpoint) }
                    };

                    // Initialize chat history and transcript cache
                    chatHistoryCache[contextId] = new List<ChatMessage>();
                    transcriptCache[contextId] = new StringBuilder();
                    conversationState[contextId] = "Initial"; // Starting state
                   
                    // Add call start information to transcript
                    transcriptCache[contextId].AppendLine($"[CALL_START] {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    transcriptCache[contextId].AppendLine($"[CALL_ID] {contextId}");
                    transcriptCache[contextId].AppendLine($"[CALLER_NUMBER] {incomingCallEventData.FromCommunicationIdentifier.PhoneNumber.Value}");
                   
                    AnswerCallResult answerCallResult = await callAutomationClient.AnswerCallAsync(options);

                    return Results.Ok();
                }
            }
        }
    }
    return Results.Ok();
});

//Handle call back event such as recognize the speech of the customer, or call connected. These events are sent by the Event grid you need to configure in the ACS resource.
app.MapPost("/api/callbacks/{contextId}", async (CloudEvent[] cloudEvents, ILogger<Program> logger, [FromRoute] string contextId,
    [Required] string callerId) => {
        foreach (var cloudEvent in cloudEvents)
        {
            var parsedEvent = CallAutomationEventParser.Parse(cloudEvent);
            logger.LogInformation($"{parsedEvent?.GetType().Name} parsedEvent received for call connection id: {parsedEvent?.CallConnectionId}");
            var callConnection = callAutomationClient.GetCallConnection(parsedEvent.CallConnectionId);

           
            if (parsedEvent is CallConnected)
            {
                // Modified system prompt to include German instructions
                chatHistoryCache[contextId].Add(new SystemChatMessage(GermanHelper.systemPrompt));
               
                // Add system prompt to transcript for reference
                transcriptCache[contextId].AppendLine($"[SYSTEM_PROMPT]\n{GermanHelper.systemPrompt}");

                // Introduction message asking for consent in German
                var connectMessage = "Hallo! Ich bin Sophia, eine Wissenstransfer-Assistentin, die mit Ihrem Unternehmen zusammenarbeitet, um bei der Erstellung von Schulungsmaterialien zu helfen. Vielen Dank, dass Sie sich die Zeit nehmen. Bevor wir beginnen, möchte ich fragen, ob es in Ordnung ist, dass dieses Gespräch aufgezeichnet und mit KI verarbeitet wird?";
                chatHistoryCache[contextId].Add(new AssistantChatMessage(connectMessage));
               
                // Add to transcript
                transcriptCache[contextId].AppendLine($"[Sophia] {connectMessage}");
               
                // Set conversation state to waiting for consent
                conversationState[contextId] = "AwaitingConsent";
               
                // Increased silence timeout for consent
                await SayAndRecognizeAsync(callConnection.GetCallMedia(), new PhoneNumberIdentifier(callerId), connectMessage, 2000);
            }

            // Handle silence or recognition failure
            if (parsedEvent is RecognizeFailed recognizeFailed &&
                recognizeFailed.ResultInformation != null &&
                recognizeFailed.ResultInformation.SubCode != null)
            {
                string currentState = conversationState.ContainsKey(contextId) ? conversationState[contextId] : "Unknown";
                logger.LogInformation($"Recognize failed in state: {currentState}, SubCode: {recognizeFailed.ResultInformation.SubCode}");
               
                if (MediaEventReasonCode.RecognizeInitialSilenceTimedOut.Equals(recognizeFailed.ResultInformation.SubCode.ToString()))
                {
                    // Different handling based on conversation state
                    if (currentState == "AwaitingConsent")
                    {
                        var consentPrompt = "Ich habe Ihre Antwort nicht gehört. Um mit dem Interview fortzufahren, benötige ich Ihre Zustimmung. Ist es in Ordnung, wenn dieses Gespräch aufgezeichnet und mit KI verarbeitet wird? Bitte sagen Sie ja oder nein.";
                        transcriptCache[contextId].AppendLine($"[Sophia] {consentPrompt}");
                        chatHistoryCache[contextId].Add(new AssistantChatMessage(consentPrompt));
                        await SayAndRecognizeAsync(callConnection.GetCallMedia(), new PhoneNumberIdentifier(callerId), consentPrompt, 2000);
                    }
                    else if (currentState == "ConsentDeclined")
                    {
                        // Already declining, just end the call
                        await SaveTranscriptToBlobStorage(contextId, transcriptCache[contextId].ToString());
                        callConnection.HangUp(true);
                    }
                    else if (currentState == "ConsentGiven")
                    {
                        var nextPrompt = "Lassen Sie mich Sie nach Ihrer Rolle fragen. Könnten Sie mir sagen, in welcher dieser Rollen Sie Erfahrung haben: Rezeptionist/in, Sekretär/in oder Pflegeassistent/in?";
                        transcriptCache[contextId].AppendLine($"[Sophia] {nextPrompt}");
                        chatHistoryCache[contextId].Add(new AssistantChatMessage(nextPrompt));
                        conversationState[contextId] = "RoleIdentification";
                        await SayAndRecognizeAsync(callConnection.GetCallMedia(), new PhoneNumberIdentifier(callerId), nextPrompt, 1000);
                    }
                    else
                    {
                        // Any other state, prompt for continuation
                        var noResponse = "Es tut mir leid, ich habe nichts gehört. Könnten Sie bitte antworten, damit wir unser Gespräch fortsetzen können?";
                        transcriptCache[contextId].AppendLine($"[Sophia] {noResponse}");
                        chatHistoryCache[contextId].Add(new AssistantChatMessage(noResponse));
                        await SayAndRecognizeAsync(callConnection.GetCallMedia(), new PhoneNumberIdentifier(callerId), noResponse, 1000);
                    }
                }
                else
                {
                    // Other recognition failures
                    var tryAgain = "Es tut mir leid, ich habe Schwierigkeiten zu verstehen. Könnten Sie bitte nochmal sprechen?";
                    transcriptCache[contextId].AppendLine($"[Sophia] {tryAgain}");
                    chatHistoryCache[contextId].Add(new AssistantChatMessage(tryAgain));
                    await SayAndRecognizeAsync(callConnection.GetCallMedia(), new PhoneNumberIdentifier(callerId), tryAgain, 1000);
                }
            }

            // This event is generated when the speech is recorded by call automation service
            if (parsedEvent is RecognizeCompleted recogEvent
                && recogEvent.RecognizeResult is SpeechResult speech_result)
            {
                string userSpeech = speech_result.Speech;
                logger.LogInformation($"User said: {userSpeech}");
               
                // Add user's speech to transcript
                transcriptCache[contextId].AppendLine($"[User] {userSpeech}");
                chatHistoryCache[contextId].Add(new UserChatMessage(userSpeech));
               
                // Get current conversation state
                string currentState = conversationState.ContainsKey(contextId) ? conversationState[contextId] : "Unknown";
                logger.LogInformation($"Current conversation state: {currentState}");
               
                // Handle the user's response based on conversation state
                if (currentState == "AwaitingConsent")
                {
                    // Handle consent response with German keywords
                    if (IsConsentGiven(userSpeech))
                    {
                        // User gave consent
                        conversationState[contextId] = "ConsentGiven";
                        var consentResponse = "Vielen Dank für Ihre Zustimmung. Jetzt möchte ich wissen, in welcher Rolle Sie Erfahrung haben: Rezeptionist/in, Sekretär/in oder Pflegeassistent/in?";
                        transcriptCache[contextId].AppendLine($"[Sophia] {consentResponse}");
                        chatHistoryCache[contextId].Add(new AssistantChatMessage(consentResponse));
                        await SayAndRecognizeAsync(callConnection.GetCallMedia(), new PhoneNumberIdentifier(callerId), consentResponse, 1000);
                    }
                    else if (IsConsentDeclined(userSpeech))
                    {
                        // User declined consent
                        conversationState[contextId] = "ConsentDeclined";
                        var declineResponse = "Ich verstehe und respektiere Ihre Entscheidung. Wir können ohne Ihre Zustimmung nicht fortfahren. Vielen Dank für Ihre Zeit und auf Wiedersehen.";
                        transcriptCache[contextId].AppendLine($"[Sophia] {declineResponse}");
                        chatHistoryCache[contextId].Add(new AssistantChatMessage(declineResponse));
                        await SayAsync(callConnection.GetCallMedia(), new PhoneNumberIdentifier(callerId), declineResponse);
                       
                        // Save transcript and end call after a delay
                        await Task.Delay(3000);
                        await SaveTranscriptToBlobStorage(contextId, transcriptCache[contextId].ToString());
                        callConnection.HangUp(true);
                        return;
                    }
                    else
                    {
                        // Unclear response, ask again
                        var clarifyConsent = "Es tut mir leid, ich habe Ihre Antwort nicht verstanden. Könnten Sie bitte deutlich 'ja' sagen, wenn Sie damit einverstanden sind, dass dieses Gespräch aufgezeichnet und verarbeitet wird, oder 'nein', wenn Sie nicht einverstanden sind?";
                        transcriptCache[contextId].AppendLine($"[Sophia] {clarifyConsent}");
                        chatHistoryCache[contextId].Add(new AssistantChatMessage(clarifyConsent));
                        await SayAndRecognizeAsync(callConnection.GetCallMedia(), new PhoneNumberIdentifier(callerId), clarifyConsent, 1000);
                    }
                    return; // Stop processing this event after handling consent
                }

                // If user wants to end the call - German keywords
                if (userSpeech.ToLower().Contains("auf wiedersehen") ||
                    userSpeech.ToLower().Contains("tschüss") ||
                    userSpeech.ToLower().Contains("anruf beenden") ||
                    userSpeech.ToLower().Contains("auflegen"))
                {
                    var goodbyeMessage = "Vielen Dank, dass Sie Ihr Wissen und Ihre Erfahrungen mit uns geteilt haben! Diese Informationen werden unglaublich hilfreich sein. Ich danke Ihnen für Ihre Zeit heute. Auf Wiedersehen!";
                   
                    transcriptCache[contextId].AppendLine($"[Sophia] {goodbyeMessage}");
                    await SayAsync(callConnection.GetCallMedia(), new PhoneNumberIdentifier(callerId), goodbyeMessage);
                    // Save the transcript before ending the call
                    await SaveTranscriptToBlobStorage(contextId, transcriptCache[contextId].ToString());
                    // Wait a moment for the message to complete, then hang up
                    await Task.Delay(3000);
                    callConnection.HangUp(true);
                    return;
                }
                // Only proceed with normal conversation if consent has been given
                if (currentState != "ConsentGiven" && currentState != "RoleIdentification" &&
                    currentState != "Interview" && !currentState.StartsWith("Role"))
                {
                    logger.LogInformation($"Skipping normal conversation flow because current state is: {currentState}");
                    return;
                }
               
                // Process role identification if in that state - with German role names
                if (currentState == "RoleIdentification")
                {
                    if (userSpeech.ToLower().Contains("rezeption") || 
                        userSpeech.ToLower().Contains("empfang"))
                    {
                        conversationState[contextId] = "RoleReceptionist";
                    }
                    else if (userSpeech.ToLower().Contains("sekretär") || 
                             userSpeech.ToLower().Contains("sekretärin"))
                    {
                        conversationState[contextId] = "RoleSecretary";
                    }
                    else if (userSpeech.ToLower().Contains("pflege") ||
                             userSpeech.ToLower().Contains("gesundheit") ||
                             userSpeech.ToLower().Contains("assistent") ||
                             userSpeech.ToLower().Contains("assistentin"))
                    {
                        conversationState[contextId] = "RoleHealthcare";
                    }
                   
                    // Set state to Interview after identifying role
                    if (conversationState[contextId].StartsWith("Role"))
                    {
                        conversationState[contextId] = "Interview";
                    }
                }
                // Recreate phoneId identifier for use within this scope
                var phoneId = new PhoneNumberIdentifier(callerId);
                // Add reminder prompt for GPT
                chatHistoryCache[contextId].Add(new UserChatMessage(GermanHelper.reminderprompt));
                try
                {
                    // Calling Azure Open AI to get a response
                    StringBuilder gptBuffer = new();
                    StringBuilder fullGptResponse = new(); // To store the full response for chat history
                    logger.LogInformation("Starting OpenAI call");
                    await foreach (StreamingChatCompletionUpdate update in chatClient.CompleteChatStreamingAsync(chatHistoryCache[contextId], options))
                    {
                        var message = update.ContentUpdate;
                        foreach (var item in message)
                        {
                            if (string.IsNullOrEmpty(item.Text))
                            {
                                continue;
                            }
                            gptBuffer.Append(item.Text);
                            fullGptResponse.Append(item.Text);
                            if (sentenceSeparators.Any(item.Text.Contains))
                            {
                                var sentence = Regex.Replace(gptBuffer.ToString().Trim(), @"\[doc\d+\]", string.Empty);
                                if (!string.IsNullOrEmpty(sentence))
                                {
                                    // Add assistant's spoken sentence to transcript
                                    transcriptCache[contextId].AppendLine($"[Sophia] {sentence}");
                                    await SayAsync(callConnection.GetCallMedia(), phoneId, sentence);
                                    logger.LogInformation($"Streamed: '{sentence}'");
                                    gptBuffer.Clear();
                                }
                            }
                        }
                    }
                    // Handle any remaining text in the buffer
                    if (gptBuffer.Length > 0)
                    {
                        var finalSentence = Regex.Replace(gptBuffer.ToString().Trim(), @"\[doc\d+\]", string.Empty);
                        if (!string.IsNullOrEmpty(finalSentence)) 
                        {
                            transcriptCache[contextId].AppendLine($"[Sophia] {finalSentence}");
                            await SayAsync(callConnection.GetCallMedia(), phoneId, finalSentence);
                            logger.LogInformation($"Streamed final part: '{finalSentence}'");
                        }
                    }
                    // Add the complete AI response to the chat history
                    if(fullGptResponse.Length > 0)
                    {
                        chatHistoryCache[contextId].Add(new AssistantChatMessage(fullGptResponse.ToString()));
                    }
                    // Start listening for the user's response directly
                    var recognizeOptions = new CallMediaRecognizeSpeechOptions(
                        targetParticipant: CommunicationIdentifier.FromRawId(phoneId.RawId))
                    {
                        InitialSilenceTimeout = TimeSpan.FromSeconds(10),
                        EndSilenceTimeout = TimeSpan.FromMilliseconds(1000)
                    };
                    logger.LogInformation($"AI response finished. Starting recognition for user answer. ContextId: {contextId}");
                    await callConnection.GetCallMedia().StartRecognizingAsync(recognizeOptions);
                }
                catch (Exception ex)
                {
                    logger.LogError($"Error processing OpenAI response: {ex.Message}");
                    var errorMessage = "Es tut mir leid, ich habe Schwierigkeiten, Ihre Antwort zu verarbeiten. Könnten wir es noch einmal versuchen?";
                    transcriptCache[contextId].AppendLine($"[ERROR] {ex.Message}");
                    transcriptCache[contextId].AppendLine($"[Sophia] {errorMessage}");
                    await SayAndRecognizeAsync(callConnection.GetCallMedia(), phoneId, errorMessage, 1000);
                }
            }
           
            // Handle call disconnected - save transcript
            if (parsedEvent is CallDisconnected)
            {
                logger.LogInformation($"Call disconnected for context ID: {contextId}");
               
                // Add call end information to transcript
                transcriptCache[contextId].AppendLine($"[CALL_END] {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
               
                // Save transcript to Azure Blob Storage
                await SaveTranscriptToBlobStorage(contextId, transcriptCache[contextId].ToString());
            }
        }
    });

app.Run();

TextSource CreateTextSource(string response) => new TextSource(response) { VoiceName = speechVoiceName };

// Convert the message to speech and play on the connected call
async Task SayAsync(CallMedia callConnectionMedia, PhoneNumberIdentifier phoneId, string response)
{
    var responseTextSource = CreateTextSource(response);
    var recognize_result = await callConnectionMedia.PlayToAllAsync(new PlayToAllOptions([responseTextSource]));
}

// Modified to include configurable timeout
async Task SayAndRecognizeAsync(CallMedia callConnectionMedia, PhoneNumberIdentifier phoneId, string response, int timeoutMs = 500)
{
    var responseTextSource = CreateTextSource(response);

    var recognizeOptions =
        new CallMediaRecognizeSpeechOptions(
            targetParticipant: CommunicationIdentifier.FromRawId(phoneId.RawId))
        {
            Prompt = responseTextSource,
            EndSilenceTimeout = TimeSpan.FromMilliseconds(timeoutMs), // Configurable timeout
            InitialSilenceTimeout = TimeSpan.FromSeconds(10) // 10 seconds for initial silence
        };

    var recognize_result = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
}

// Save transcript to Azure Blob Storage
async Task SaveTranscriptToBlobStorage(string contextId, string transcript)
{
    try
    {
        // Create BlobServiceClient
        var blobServiceClient = new BlobServiceClient(blobStorageConnectionString);
       
        // Get container and create if it doesn't exist
        var containerClient = blobServiceClient.GetBlobContainerClient("transcript");
        await containerClient.CreateIfNotExistsAsync();
       
        // Generate unique filename
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var filename = $"{contextId}-{timestamp}.txt";
       
        // Get blob client and upload transcript
        var blobClient = containerClient.GetBlobClient(filename);
       
        // Add metadata
        var metadata = new Dictionary<string, string>
        {
            ["callId"] = contextId,
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
            ["callType"] = "inbound-sop"
        };
       
        // Upload transcript with metadata
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(transcript));
        await blobClient.UploadAsync(stream, new BlobUploadOptions { Metadata = metadata });
       
        Console.WriteLine($"Transcript saved to blob storage: {filename}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error saving transcript: {ex.Message}");
    }
}

// Helper functions to detect consent - updated for German
bool IsConsentGiven(string response)
{
    string lowerResponse = response.ToLower();
    return lowerResponse.Contains("ja") ||
           lowerResponse.Contains("sicher") ||
           lowerResponse.Contains("okay") ||
           lowerResponse.Contains("ok") ||
           lowerResponse.Contains("in ordnung") ||
           lowerResponse.Contains("ich stimme zu") ||
           lowerResponse.Contains("einverstanden") ||
           lowerResponse.Contains("können sie") ||
           lowerResponse.Contains("klar");
}

bool IsConsentDeclined(string response)
{
    string lowerResponse = response.ToLower();
    return lowerResponse.Contains("nein") ||
           lowerResponse.Contains("nicht") ||
           lowerResponse.Contains("keine") ||
           lowerResponse.Contains("kein") ||
           lowerResponse.Contains("ablehnen") ||
           lowerResponse.Contains("nicht einverstanden") ||
           lowerResponse.Contains("verweigere") ||
           lowerResponse.Contains("lehne ab");
}

// Define the Program class to be used with User Secrets
public partial class Program { }
