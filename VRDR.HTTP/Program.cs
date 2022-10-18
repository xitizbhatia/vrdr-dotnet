using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace VRDR.HTTP
{
    public class Program
    {
        public VRDRListener Listener;

        public Program()
        {
            Listener = new VRDRListener(SendResponse, "http://*:8085/");
        }

        public void Start()
        {
            Listen();
            ManualResetEvent _quitEvent = new ManualResetEvent(false);
            _quitEvent.WaitOne();
            Stop();
        }

        public void Listen()
        {
            Listener.Run();
        }

        public void Stop()
        {
            Listener.Stop();
        }

        static void Main(string[] args)
        {
            Program program = new Program();
            program.Start();
        }

        public static string SendResponse(HttpListenerRequest request)
        {
            string requestBody = GetBodyContent(request);
            DeathRecord deathRecord = null;
            Bundle searchSets = null;
            Console.WriteLine($"Request from: {request.UserHostAddress}, type: {request.ContentType}, url: {request.RawUrl}.");

            // Look at content type to determine input format; be permissive in what we accept as format specification
            switch (request.ContentType)
            {
                case string ijeType when new Regex(@"ije").IsMatch(ijeType): // application/ije
                    IJEMortality ije = new IJEMortality(requestBody);
                    deathRecord = ije.ToDeathRecord();
                    break;
                case string nightingaleType when new Regex(@"nightingale").IsMatch(nightingaleType):
                    deathRecord = Nightingale.FromNightingale(requestBody);
                    break;
                case string fhirResponseBundleOfBundle when new Regex(@"responsebundle").IsMatch(fhirResponseBundleOfBundle): //application/fhir+responsebundle
                    FhirJsonParser parser = new FhirJsonParser();
                    searchSets = parser.Parse<Bundle>(requestBody);
                    break;
                case string jsonType when new Regex(@"json").IsMatch(jsonType): // application/fhir+json
                case string xmlType when new Regex(@"xml").IsMatch(xmlType): // application/fhir+xml
                default:
                    deathRecord = new DeathRecord(requestBody);
                    break;
            }

            // Look at URL extension to determine output format; be permissive in what we accept as format specification
            string result = "";
            switch (request.RawUrl)
            {
                case string url when new Regex(@"(ije|mor)$").IsMatch(url): // .mor or .ije
                    IJEMortality ije = new IJEMortality(deathRecord);
                    result = ije.ToString();
                    break;
                case string url when new Regex(@"json$").IsMatch(url): // .json
                    result = deathRecord.ToJSON();
                    break;
                case string url when new Regex(@"fhir").IsMatch(url): // .fhir
                    DeathRecordSubmissionMessage message = new DeathRecordSubmissionMessage(deathRecord);
                    message.MessageSource = "https://dev.vrvweb.com/vrv/fhir";
                    result = message.ToJSON(true);
                    break;
                case string url when new Regex(@"xml$").IsMatch(url): // .xml
                    result = deathRecord.ToXML();
                    break;
                case string url when new Regex(@"nightingale$").IsMatch(url): // .nightingale
                    result = Nightingale.ToNightingale(deathRecord);
                    break;
                case string url when new Regex(@"parsebundleofbundles").IsMatch(url): // .parsebundleofbundles
                    result = parseBundle(searchSets);
                    break;
                case string url when new Regex(@"extractmessagefrombundle").IsMatch(url): // .extractmessagefrombundle
                    result = extractMessageFromBundle(searchSets, request.Headers.GetValues("FHIR-MESSAGE-ID").FirstOrDefault());
                    break;
            }

            return result;
        }

        public static string GetBodyContent(HttpListenerRequest request)
        {
            using (Stream body = request.InputStream)
            {
                using (StreamReader reader = new StreamReader(body, request.ContentEncoding))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static string parseBundle(Bundle bundleOfBundles)
        {
            BundleResponse bundleResponse = new BundleResponse
            {
                messages = new List<Response>()
            };
            Response response = null;
            foreach (var entry in bundleOfBundles.Entry)
            {
                try
                {
                    BaseMessage msg = BaseMessage.Parse<BaseMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);

                    switch (msg.MessageType)
                    {
                        case "http://nchs.cdc.gov/vrdr_acknowledgement":
                            AcknowledgementMessage message = BaseMessage.Parse<AcknowledgementMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            Console.WriteLine($"*** Received ack message: {message.MessageId} for {message.AckedMessageId} and certNo: {message.CertNo}");
                            response = new Response { messageId = message.MessageId, type = "ACKNOWLEDGEMENT", reference = message.AckedMessageId };
                            bundleResponse.messages.Add(response);
                            //ProcessAckMessage(message);
                            break;
                        case "http://nchs.cdc.gov/vrdr_causeofdeath_coding":
                            CauseOfDeathCodingMessage codCodeMsg = BaseMessage.Parse<CauseOfDeathCodingMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            Console.WriteLine($"*** Received coding message: {codCodeMsg.MessageId} for {codCodeMsg.CodedMessageId}");
                            response = new Response { messageId = codCodeMsg.MessageId, type = "COD_CODING", reference = codCodeMsg.CodedMessageId };
                            bundleResponse.messages.Add(response);
                            //ProcessResponseMessage(codCodeMsg);
                            break;
                        case "http://nchs.cdc.gov/vrdr_demographics_coding":
                            DemographicsCodingMessage demCodeMsg = BaseMessage.Parse<DemographicsCodingMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            Console.WriteLine($"*** Received demographics coding message: {demCodeMsg.MessageId} for {demCodeMsg.CodedMessageId}");
                            response = new Response { messageId = demCodeMsg.MessageId, type = "DEMOGRAPHIC_CODING", reference = demCodeMsg.CodedMessageId };
                            bundleResponse.messages.Add(response);
                            //ProcessResponseMessage(demCodeMsg);
                            break;
                        case "http://nchs.cdc.gov/vrdr_causeofdeath_coding_update":
                            CauseOfDeathCodingUpdateMessage codUpdateMsg = BaseMessage.Parse<CauseOfDeathCodingUpdateMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            Console.WriteLine($"*** Received coding update message: {codUpdateMsg.MessageId} for {codUpdateMsg.CodedMessageId}");
                            response = new Response { messageId = codUpdateMsg.MessageId, type = "COD_CODING_UPDATE", reference = codUpdateMsg.CodedMessageId };
                            bundleResponse.messages.Add(response);
                            //ProcessResponseMessage(codUpdateMsg);
                            break;
                        case "http://nchs.cdc.gov/vrdr_demographics_coding_update":
                            DemographicsCodingUpdateMessage demUpdateMsg = BaseMessage.Parse<DemographicsCodingUpdateMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            Console.WriteLine($"*** Received demographics coding update message: {demUpdateMsg.MessageId} for {demUpdateMsg.CodedMessageId}");
                            response = new Response { messageId = demUpdateMsg.MessageId, type = "DEMOGRAPHIC_CODING_UPDATE", reference = demUpdateMsg.CodedMessageId };
                            bundleResponse.messages.Add(response);
                            //ProcessResponseMessage(demUpdateMsg);
                            break;
                        case "http://nchs.cdc.gov/vrdr_extraction_error":
                            ExtractionErrorMessage errMsg = BaseMessage.Parse<ExtractionErrorMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            Console.WriteLine($"*** Received extraction error: {errMsg.MessageId}  for {errMsg.FailedMessageId}");
                            response = new Response { messageId = errMsg.MessageId, type = "EXTRACTION_ERROR_MESSAGE", reference = errMsg.FailedMessageId };
                            bundleResponse.messages.Add(response);
                            //ProcessResponseMessage(errMsg);
                            break;
                        default:
                            Console.WriteLine($"*** Unknown message type");
                            break;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"*** Error parsing message: {e}");
                    // Extraction errors require acks so we insert them in the DB to send with other messages to NCHS
                    // Wrap this in another try catch so we can see any failures to create the extraction error in our logs
                    //TBD
                }
            }

            return JsonSerializer.Serialize<BundleResponse>(bundleResponse);
        }

        private static string extractMessageFromBundle(Bundle bundleOfBundles, string messageId)
        {
            string extractedMessageJSON = "";

            foreach (var entry in bundleOfBundles.Entry)
            {
                try
                {
                    BaseMessage msg = BaseMessage.Parse<BaseMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);

                    switch (msg.MessageType)
                    {
                        case "http://nchs.cdc.gov/vrdr_acknowledgement":
                            AcknowledgementMessage message = BaseMessage.Parse<AcknowledgementMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            Console.WriteLine($"*** Checking ack message: {message.MessageId} for {message.AckedMessageId} and certNo: {message.CertNo}");
                            if (message.MessageId.Equals(messageId))
                                extractedMessageJSON = message.ToJSON();
                            break;
                        case "http://nchs.cdc.gov/vrdr_causeofdeath_coding":
                            CauseOfDeathCodingMessage codCodeMsg = BaseMessage.Parse<CauseOfDeathCodingMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            Console.WriteLine($"*** Checking coding message: {codCodeMsg.MessageId} for {codCodeMsg.CodedMessageId}");
                            if (codCodeMsg.MessageId.Equals(messageId))
                                extractedMessageJSON = codCodeMsg.ToJSON();
                            break;
                        case "http://nchs.cdc.gov/vrdr_demographics_coding":
                            DemographicsCodingMessage demCodeMsg = BaseMessage.Parse<DemographicsCodingMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            Console.WriteLine($"*** Checking demographics coding message: {demCodeMsg.MessageId} for {demCodeMsg.CodedMessageId}");
                            if (demCodeMsg.MessageId.Equals(messageId))
                                extractedMessageJSON = demCodeMsg.ToJSON();
                            break;
                        case "http://nchs.cdc.gov/vrdr_causeofdeath_coding_update":
                            CauseOfDeathCodingUpdateMessage codUpdateMsg = BaseMessage.Parse<CauseOfDeathCodingUpdateMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            Console.WriteLine($"*** Checking coding update message: {codUpdateMsg.MessageId} for {codUpdateMsg.CodedMessageId}");
                            if (codUpdateMsg.MessageId.Equals(messageId))
                                extractedMessageJSON = codUpdateMsg.ToJSON();
                            break;
                        case "http://nchs.cdc.gov/vrdr_demographics_coding_update":
                            DemographicsCodingUpdateMessage demUpdateMsg = BaseMessage.Parse<DemographicsCodingUpdateMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            Console.WriteLine($"*** Checking demographics coding update message: {demUpdateMsg.MessageId} for {demUpdateMsg.CodedMessageId}");
                            if (demUpdateMsg.MessageId.Equals(messageId))
                                extractedMessageJSON = demUpdateMsg.ToJSON();
                            break;
                        case "http://nchs.cdc.gov/vrdr_extraction_error":
                            ExtractionErrorMessage errMsg = BaseMessage.Parse<ExtractionErrorMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            Console.WriteLine($"*** Checking extraction error: {errMsg.MessageId}  for {errMsg.FailedMessageId}");
                            if (errMsg.MessageId.Equals(messageId))
                                extractedMessageJSON = errMsg.ToJSON();
                            break;
                        default:
                            Console.WriteLine($"*** Unknown message type");
                            break;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"*** Error parsing message: {e}");
                    // Extraction errors require acks so we insert them in the DB to send with other messages to NCHS
                    // Wrap this in another try catch so we can see any failures to create the extraction error in our logs
                    //TBD
                }
            }

            return extractedMessageJSON;
        }
    }
}