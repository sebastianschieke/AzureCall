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
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
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
var speechVoiceName = "en-US-JennyMultilingualV2Neural";

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

// Dictionary to store conversation transcripts
ConcurrentDictionary<string, StringBuilder> transcriptCache = new();
ConcurrentDictionary<string, List<ChatMessage>> chatHistoryCache = new();
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
                chatHistoryCache[contextId].Add(new SystemChatMessage(Helper.systemPrompt));
                
                // Add system prompt to transcript for reference
                transcriptCache[contextId].AppendLine($"[SYSTEM_PROMPT]\n{Helper.systemPrompt}");

                // Introduction message from Chloe
                var connectMessage = "Hello! I'm Chloe, the knowledge transfer assistant working with your company to help prepare training materials for the new receptionist. Thank you so much for taking the time to speak with me today. I'd like to learn from your experience to make sure we capture all the important details that might not be in the official manuals. Is now still a good time to chat?";
                chatHistoryCache[contextId].Add(new AssistantChatMessage(connectMessage));
                
                // Add to transcript
                transcriptCache[contextId].AppendLine($"[Chloe] {connectMessage}");
                
                await SayAndRecognizeAsync(callConnection.GetCallMedia(), new PhoneNumberIdentifier(callerId), connectMessage);
            }

            // Handle silence or recognition failure
            if (parsedEvent is RecognizeFailed recognizeFailed && 
                recognizeFailed.ResultInformation != null && 
                recognizeFailed.ResultInformation.SubCode != null && 
                MediaEventReasonCode.RecognizeInitialSilenceTimedOut.Equals(recognizeFailed.ResultInformation.SubCode.ToString()))
            {
                Console.WriteLine($"Recognize failed: {parsedEvent.ResultInformation}");
                var noResponse = "I'm sorry, I didn't hear anything. If you'd like, we can start by having you describe what a typical day looks like for you as the receptionist, from when you first arrive to when you leave. Or we can focus on any specific area of the role that you think is important for a new person to know.";
                
                // Add to transcript
                transcriptCache[contextId].AppendLine($"[Chloe] {noResponse}");
                
                await SayAndRecognizeAsync(callConnection.GetCallMedia(), new PhoneNumberIdentifier(callerId), noResponse);
                chatHistoryCache[contextId].Add(new AssistantChatMessage(noResponse));
            }

            // This event is generated when the speech is recorded by call automation service. When the user has completed their sentence
            if (parsedEvent is RecognizeCompleted recogEvent
                && recogEvent.RecognizeResult is SpeechResult speech_result)
            {
                 // Add user's speech to transcript
                 transcriptCache[contextId].AppendLine($"[User] {speech_result.Speech}");
                 
                 chatHistoryCache[contextId].Add(new UserChatMessage(speech_result.Speech));
                 chatHistoryCache[contextId].Add(new UserChatMessage(Helper.reminderprompt));

                // If user says goodbye, end the call with a final message
                if (speech_result.Speech.ToLower().Contains("goodbye") || 
                    speech_result.Speech.ToLower().Contains("bye") || 
                    speech_result.Speech.ToLower().Contains("end this call") ||
                    speech_result.Speech.ToLower().Contains("hang up"))
                {
                    var goodbyeMessage = "Thank you so much for sharing your knowledge and experience! This information will be incredibly helpful for the new receptionist. I appreciate your time today. Goodbye!";
                    
                    // Add to transcript
                    transcriptCache[contextId].AppendLine($"[Chloe] {goodbyeMessage}");
                    
                    await SayAsync(callConnection.GetCallMedia(), new PhoneNumberIdentifier(callerId), goodbyeMessage);
                    
                    // Save the transcript before ending the call
                    await SaveTranscriptToBlobStorage(contextId, transcriptCache[contextId].ToString());
                    
                    // Hang up the call
                    callConnection.HangUp(true);
                    return;
                }

                // Calling Azure Open AI to get a response based on the conversation history, knowledgebase and the system prompt
                StringBuilder gptBuffer = new();

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
                        if (sentenceSeparators.Any(item.Text.Contains))
                        {
                            var sentence = Regex.Replace(gptBuffer.ToString().Trim(), @"\[doc\d+\]", string.Empty);
                            if (!string.IsNullOrEmpty(sentence))
                            {
                                chatHistoryCache[contextId].Add(new AssistantChatMessage(sentence));
                                
                                // Add to transcript
                                transcriptCache[contextId].AppendLine($"[Chloe] {sentence}");
                                
                                await SayAsync(callConnection.GetCallMedia(), new PhoneNumberIdentifier(callerId), sentence);
                                Console.WriteLine($"\t > streamed: '{sentence}'");
                                gptBuffer.Clear();
                            }
                        }
                    }
                }
                await SayAndRecognizeAsync(callConnection.GetCallMedia(), new PhoneNumberIdentifier(callerId), ".");
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

async Task SayAndRecognizeAsync(CallMedia callConnectionMedia, PhoneNumberIdentifier phoneId, string response)
{
    // creates the 
    var responseTextSource = CreateTextSource(response);

    var recognizeOptions =
        new CallMediaRecognizeSpeechOptions(
            targetParticipant: CommunicationIdentifier.FromRawId(phoneId.RawId))
        {
            Prompt = responseTextSource,
            EndSilenceTimeout = TimeSpan.FromMilliseconds(500)
        };

    var recognize_result = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
}

// Save transcript to Azure Blob Storage
// Save transcript to Azure Blob Storage as DOCX
async Task SaveTranscriptToBlobStorage(string contextId, string transcript)
{
    try
    {
        // Create BlobServiceClient
        var blobServiceClient = new BlobServiceClient(blobStorageConnectionString);
        
        // Get container and create if it doesn't exist
        var containerClient = blobServiceClient.GetBlobContainerClient("transcript");
        await containerClient.CreateIfNotExistsAsync();
        
        // Generate unique filename (now with .docx extension)
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var filename = $"{contextId}-{timestamp}.docx";
        
        // Get blob client
        var blobClient = containerClient.GetBlobClient(filename);
        
        // Create DOCX document in memory
        using var memoryStream = new MemoryStream();
        
        // Create the document
        using (WordprocessingDocument document = WordprocessingDocument.Create(memoryStream, WordprocessingDocumentType.Document))
        {
            // Add a main document part
            MainDocumentPart mainPart = document.AddMainDocumentPart();
            
            // Create the document structure
            mainPart.Document = new Document();
            Body body = new Body();
            mainPart.Document.Append(body);
            
            // Split transcript by lines and add each line as a paragraph
            string[] lines = transcript.Split('\n');
            foreach (string line in lines)
            {
                var para = new Paragraph();
                var run = new Run();
                var text = new Text(line) { Space = SpaceProcessingModeValues.Preserve };
                
                // Format specific lines (optional)
                if (line.StartsWith("[CALL_START]") || 
                    line.StartsWith("[CALL_ID]") || 
                    line.StartsWith("[CALLER_NUMBER]") ||
                    line.StartsWith("[CALL_END]"))
                {
                    // Make metadata bold
                    run.RunProperties = new RunProperties(new Bold());
                }
                else if (line.StartsWith("[Chloe]"))
                {
                    // Style for assistant's messages
                    run.RunProperties = new RunProperties(
                        new Color { Val = "0000FF" }  // Blue color
                    );
                }
                else if (line.StartsWith("[User]"))
                {
                    // Style for user's messages
                    run.RunProperties = new RunProperties(
                        new Color { Val = "006400" }  // Dark green color
                    );
                }
                
                run.Append(text);
                para.Append(run);
                body.Append(para);
            }
            
            // Save the document
            document.Save();
        }
        
        // Reset the memory stream position
        memoryStream.Position = 0;
        
        // Add metadata
        var metadata = new Dictionary<string, string>
        {
            ["callId"] = contextId,
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
            ["callType"] = "inbound-sop"
        };
        
        // Upload document with metadata
        await blobClient.UploadAsync(memoryStream, new BlobUploadOptions { Metadata = metadata });
        
        Console.WriteLine($"Transcript saved to blob storage as DOCX: {filename}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error saving transcript: {ex.Message}");
    }
}
// Define the Program class to be used with User Secrets
public partial class Program { }