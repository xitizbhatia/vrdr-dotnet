using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
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
            Bundle bundle = null;

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
                    bundle = parser.Parse<Bundle>(requestBody);
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
                case string url when new Regex(@"json$").IsMatch(url): // .json //ije to json vrdr record 
                    result = deathRecord.ToJSON();
                    break;
                case string url when new Regex(@"fhir").IsMatch(url): // .fhir //ije to json vrdr message or create acknowledgement from existing json
                    string messageType = request.Headers.GetValues("FHIR-MESSAGE-TYPE").FirstOrDefault();
                    string auxiliaryId = request.Headers.GetValues("STATE-AUXILIARY-ID").FirstOrDefault();

                    if (deathRecord != null)
                    {
                        deathRecord.StateLocalIdentifier1 = auxiliaryId;
                    }

                    switch (messageType)
                    {
                        case "SUBMISSION":
                            DeathRecordSubmissionMessage submissionmessage = new DeathRecordSubmissionMessage(deathRecord);
                            submissionmessage.MessageSource = "https://dev.vrvweb.com/vrv/fhir";
                            submissionmessage.StateAuxiliaryId = auxiliaryId;
                            result = submissionmessage.ToJSON(true);
                            break;
                        case "UPDATE":
                            DeathRecordUpdateMessage updatemessage = new DeathRecordUpdateMessage(deathRecord);
                            updatemessage.MessageSource = "https://dev.vrvweb.com/vrv/fhir";
                            updatemessage.StateAuxiliaryId = auxiliaryId;
                            result = updatemessage.ToJSON(true);
                            break;
                        case "VOID":
                            DeathRecordVoidMessage voidmessage = new DeathRecordVoidMessage(deathRecord);
                            voidmessage.MessageSource = "https://dev.vrvweb.com/vrv/fhir";
                            voidmessage.StateAuxiliaryId = auxiliaryId;
                            result = voidmessage.ToJSON(true);
                            break;
                        case "ACKNOWLEDGEMENT":
                            BaseMessage baseMessage = BaseMessage.Parse(bundle);
                            AcknowledgementMessage acknowledgementMessage = new AcknowledgementMessage(baseMessage);
                            acknowledgementMessage.MessageSource = "https://dev.vrvweb.com/vrv/fhir";
                            acknowledgementMessage.StateAuxiliaryId = auxiliaryId;
                            result = acknowledgementMessage.ToJSON(true);
                            break;
                        default:
                            DeathRecordSubmissionMessage messagedefault = new DeathRecordSubmissionMessage(deathRecord);
                            messagedefault.MessageSource = "https://dev.vrvweb.com/vrv/fhir";
                            messagedefault.StateAuxiliaryId = auxiliaryId;
                            result = messagedefault.ToJSON(true);
                            break;
                    }
                    break;
                case string url when new Regex(@"xml$").IsMatch(url): // .xml
                    result = deathRecord.ToXML();
                    break;
                case string url when new Regex(@"nightingale$").IsMatch(url): // .nightingale
                    result = Nightingale.ToNightingale(deathRecord);
                    break;
                case string url when new Regex(@"parsebundleofbundles").IsMatch(url): // .parsebundleofbundles
                    result = parseBundle(bundle);
                    break;
                case string url when new Regex(@"extractmessagefrombundle").IsMatch(url): // .extractmessagefrombundle
                    result = extractMessageFromBundle(bundle, request.Headers.GetValues("FHIR-MESSAGE-ID").FirstOrDefault());
                    break;
                case string url when new Regex(@"extractdiagnosticfrombundle").IsMatch(url): //.extractdiagnosticfrombundle
                    result = extractDiagnosticIssuesFromBundle(bundle,request.Headers.GetValues("MESSAGE-TYPE").FirstOrDefault());
                    break;
                case string url when new Regex(@"trx").IsMatch(url): // .trx
                    result = json2trx(bundle);
                    break;
                case string url when new Regex(@"mre").IsMatch(url): // .mre
                    result = json2mre(bundle);
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
                            //Console.WriteLine($"*** Received ack message: {message.MessageId} for {message.AckedMessageId} and certNo: {message.CertNo}");
                            response = new Response { messageId = message.MessageId, type = "ACKNOWLEDGEMENT", reference = message.AckedMessageId };
                            bundleResponse.messages.Add(response);
                            //ProcessAckMessage(message);
                            break;
                        case "http://nchs.cdc.gov/vrdr_causeofdeath_coding":
                            CauseOfDeathCodingMessage codCodeMsg = BaseMessage.Parse<CauseOfDeathCodingMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            //Console.WriteLine($"*** Received coding message: {codCodeMsg.MessageId} for {codCodeMsg.CodedMessageId}");
                            response = new Response { messageId = codCodeMsg.MessageId, type = "COD_CODING", reference = codCodeMsg.CodedMessageId };
                            bundleResponse.messages.Add(response);
                            //ProcessResponseMessage(codCodeMsg);
                            break;
                        case "http://nchs.cdc.gov/vrdr_demographics_coding":
                            DemographicsCodingMessage demCodeMsg = BaseMessage.Parse<DemographicsCodingMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            //Console.WriteLine($"*** Received demographics coding message: {demCodeMsg.MessageId} for {demCodeMsg.CodedMessageId}");
                            response = new Response { messageId = demCodeMsg.MessageId, type = "DEMOGRAPHIC_CODING", reference = demCodeMsg.CodedMessageId };
                            bundleResponse.messages.Add(response);
                            //ProcessResponseMessage(demCodeMsg);
                            break;
                        case "http://nchs.cdc.gov/vrdr_causeofdeath_coding_update":
                            CauseOfDeathCodingUpdateMessage codUpdateMsg = BaseMessage.Parse<CauseOfDeathCodingUpdateMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            //Console.WriteLine($"*** Received coding update message: {codUpdateMsg.MessageId} for {codUpdateMsg.CodedMessageId}");
                            response = new Response { messageId = codUpdateMsg.MessageId, type = "COD_CODING_UPDATE", reference = codUpdateMsg.CodedMessageId };
                            bundleResponse.messages.Add(response);
                            //ProcessResponseMessage(codUpdateMsg);
                            break;
                        case "http://nchs.cdc.gov/vrdr_demographics_coding_update":
                            DemographicsCodingUpdateMessage demUpdateMsg = BaseMessage.Parse<DemographicsCodingUpdateMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            //Console.WriteLine($"*** Received demographics coding update message: {demUpdateMsg.MessageId} for {demUpdateMsg.CodedMessageId}");
                            response = new Response { messageId = demUpdateMsg.MessageId, type = "DEMOGRAPHIC_CODING_UPDATE", reference = demUpdateMsg.CodedMessageId };
                            bundleResponse.messages.Add(response);
                            //ProcessResponseMessage(demUpdateMsg);
                            break;
                        case "http://nchs.cdc.gov/vrdr_extraction_error":
                            ExtractionErrorMessage errMsg = BaseMessage.Parse<ExtractionErrorMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            //Console.WriteLine($"*** Received extraction error: {errMsg.MessageId}  for {errMsg.FailedMessageId}");
                            response = new Response { messageId = errMsg.MessageId, type = "EXTRACTION_ERROR_MESSAGE", reference = errMsg.FailedMessageId };
                            bundleResponse.messages.Add(response);
                            //ProcessResponseMessage(errMsg);
                            break;
                        case "http://nchs.cdc.gov/vrdr_status":
                            StatusMessage statusMsg = BaseMessage.Parse<StatusMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            Console.WriteLine($"*** Received status message: {statusMsg.MessageId}  for {statusMsg.StatusedMessageId} with status: {statusMsg.Status} .");
                            if (String.Equals("noCodingNeeded_Duplicate", statusMsg.Status, StringComparison.OrdinalIgnoreCase)) {
                                response = new Response { messageId = statusMsg.MessageId, type = "STATUS_MESSAGE_NO_COD_CODING_NEEDED_DUPLICATE", reference = statusMsg.StatusedMessageId };
                            } else if (String.Equals("manualCauseOfDeathCoding", statusMsg.Status, StringComparison.OrdinalIgnoreCase)) {
                                response = new Response { messageId = statusMsg.MessageId, type = "STATUS_MESSAGE_COD_CODING_MANUAL", reference = statusMsg.StatusedMessageId };
                            } else if (String.Equals("manualDemographicCoding", statusMsg.Status, StringComparison.OrdinalIgnoreCase)) {
                                response = new Response { messageId = statusMsg.MessageId, type = "STATUS_MESSAGE_DEMOGRAPHIC_CODING_MANUAL", reference = statusMsg.StatusedMessageId };
                            } else if (String.Equals("manualCodingCanceled_Update", statusMsg.Status, StringComparison.OrdinalIgnoreCase)) {
                                response = new Response { messageId = statusMsg.MessageId, type = "STATUS_MESSAGE_COD_CODING_MANUAL_CANCELLED_UPDATE", reference = statusMsg.StatusedMessageId };
                            } else if (String.Equals("manualCodingCanceled_Void", statusMsg.Status, StringComparison.OrdinalIgnoreCase)) {
                                response = new Response { messageId = statusMsg.MessageId, type = "STATUS_MESSAGE_COD_CODING_MANUAL_CANCELLED_VOID", reference = statusMsg.StatusedMessageId };
                            } else {
                                response = new Response { messageId = statusMsg.MessageId, type = "STATUS_MESSAGE_LOOKUP_IN_JSON", reference = statusMsg.StatusedMessageId };
                            }
                            
                            bundleResponse.messages.Add(response);
                            //ProcessResponseMessage(statusMsg);
                            break;
                        case "http://nchs.cdc.gov/vrdr_submission":
                            DeathRecordSubmissionMessage submissionMessage = BaseMessage.Parse<DeathRecordSubmissionMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            response = new Response { messageId = submissionMessage.MessageId, type = "VITAL_INTERSTATE_NEW_SUBMISSION", reference = submissionMessage.MessageId }; //there is no reference for interstate messages
                            bundleResponse.messages.Add(response);
                            break;
                        case "http://nchs.cdc.gov/vrdr_submission_update":
                            DeathRecordUpdateMessage submissionUpdateMessage = BaseMessage.Parse<DeathRecordUpdateMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            response = new Response { messageId = submissionUpdateMessage.MessageId, type = "VITAL_INTERSTATE_UPDATE_SUBMISSION", reference = submissionUpdateMessage.MessageId }; //there is no reference for interstate messages
                            bundleResponse.messages.Add(response);
                            break;
                        case "http://nchs.cdc.gov/vrdr_submission_void":
                            DeathRecordVoidMessage submissionVoidMessage = BaseMessage.Parse<DeathRecordVoidMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            response = new Response { messageId = submissionVoidMessage.MessageId, type = "VITAL_INTERSTATE_VOID_SUBMISSION", reference = submissionVoidMessage.MessageId }; //there is no reference for interstate messages
                            bundleResponse.messages.Add(response);
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
                            //Console.WriteLine($"*** Checking ack message: {message.MessageId} for {message.AckedMessageId} and certNo: {message.CertNo}");
                            if (message.MessageId.Equals(messageId))
                                extractedMessageJSON = message.ToJSON();
                            break;
                        case "http://nchs.cdc.gov/vrdr_causeofdeath_coding":
                            CauseOfDeathCodingMessage codCodeMsg = BaseMessage.Parse<CauseOfDeathCodingMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            //Console.WriteLine($"*** Checking coding message: {codCodeMsg.MessageId} for {codCodeMsg.CodedMessageId}");
                            if (codCodeMsg.MessageId.Equals(messageId))
                                extractedMessageJSON = codCodeMsg.ToJSON();
                            break;
                        case "http://nchs.cdc.gov/vrdr_demographics_coding":
                            DemographicsCodingMessage demCodeMsg = BaseMessage.Parse<DemographicsCodingMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            //Console.WriteLine($"*** Checking demographics coding message: {demCodeMsg.MessageId} for {demCodeMsg.CodedMessageId}");
                            if (demCodeMsg.MessageId.Equals(messageId))
                                extractedMessageJSON = demCodeMsg.ToJSON();
                            break;
                        case "http://nchs.cdc.gov/vrdr_causeofdeath_coding_update":
                            CauseOfDeathCodingUpdateMessage codUpdateMsg = BaseMessage.Parse<CauseOfDeathCodingUpdateMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            //Console.WriteLine($"*** Checking coding update message: {codUpdateMsg.MessageId} for {codUpdateMsg.CodedMessageId}");
                            if (codUpdateMsg.MessageId.Equals(messageId))
                                extractedMessageJSON = codUpdateMsg.ToJSON();
                            break;
                        case "http://nchs.cdc.gov/vrdr_demographics_coding_update":
                            DemographicsCodingUpdateMessage demUpdateMsg = BaseMessage.Parse<DemographicsCodingUpdateMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            //Console.WriteLine($"*** Checking demographics coding update message: {demUpdateMsg.MessageId} for {demUpdateMsg.CodedMessageId}");
                            if (demUpdateMsg.MessageId.Equals(messageId))
                                extractedMessageJSON = demUpdateMsg.ToJSON();
                            break;
                        case "http://nchs.cdc.gov/vrdr_extraction_error":
                            ExtractionErrorMessage errMsg = BaseMessage.Parse<ExtractionErrorMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            //Console.WriteLine($"*** Checking extraction error: {errMsg.MessageId}  for {errMsg.FailedMessageId}");
                            if (errMsg.MessageId.Equals(messageId))
                                extractedMessageJSON = errMsg.ToJSON();
                            break;
                        case "http://nchs.cdc.gov/vrdr_status":
                            StatusMessage statusMsg = BaseMessage.Parse<StatusMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            //Console.WriteLine($"*** Received status message: {statusMsg.MessageId}  for {statusMsg.StatusedMessageId} with status: {statusMsg.Status}");
                            if (statusMsg.MessageId.Equals(messageId))
                                extractedMessageJSON = statusMsg.ToJSON();
                            break;
                        case "http://nchs.cdc.gov/vrdr_submission":
                            DeathRecordSubmissionMessage submissionMessage = BaseMessage.Parse<DeathRecordSubmissionMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            if (submissionMessage.MessageId.Equals(messageId))
                                extractedMessageJSON = submissionMessage.ToJSON();
                            break;
                        case "http://nchs.cdc.gov/vrdr_submission_update":
                            DeathRecordUpdateMessage submissionUpdateMessage = BaseMessage.Parse<DeathRecordUpdateMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            if (submissionUpdateMessage.MessageId.Equals(messageId))
                                extractedMessageJSON = submissionUpdateMessage.ToJSON();
                            break;
                        case "http://nchs.cdc.gov/vrdr_submission_void":
                            DeathRecordVoidMessage submissionVoidMessage = BaseMessage.Parse<DeathRecordVoidMessage>((Hl7.Fhir.Model.Bundle)entry.Resource);
                            if (submissionVoidMessage.MessageId.Equals(messageId))
                                extractedMessageJSON = submissionVoidMessage.ToJSON();
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

        private static string extractDiagnosticIssuesFromBundle(Bundle bundle, string messageType)
        {
            DiagnosticIssues issues = null;
            
                try
                {
                    StringBuilder sb = new StringBuilder();
                    string stateAuxiliaryId = null;
                    switch (messageType)
                    {
                        case "vrdr_extraction_error":
                            ExtractionErrorMessage errMsg = BaseMessage.Parse<ExtractionErrorMessage>((Hl7.Fhir.Model.Bundle)bundle);
                            stateAuxiliaryId = errMsg.StateAuxiliaryId;
                            foreach (var issue in errMsg.Issues)
                            {
                                sb.Append(issue.Description).Append(",");

                            }
                            break;
                        case "vrdr_status":
                            StatusMessage statusMsg = BaseMessage.Parse<StatusMessage>((Hl7.Fhir.Model.Bundle)bundle);
                            stateAuxiliaryId = statusMsg.StateAuxiliaryId;
                            sb.Append(statusMsg.Status.ToString());
                            break;
                        default:
                            Console.WriteLine($"*** Unknown message type");
                            break;

                    }
                    
                    issues = new DiagnosticIssues { stateauxid = stateAuxiliaryId, description = sb.ToString() };
                }
                catch (Exception e)
                {
                    Console.WriteLine($"*** Error parsing diagnostic issues from Error message: {e}");
                    // Extraction errors require acks so we insert them in the DB to send with other messages to NCHS
                    // Wrap this in another try catch so we can see any failures to create the extraction error in our logs
                    //TBD
                }
            

            return JsonSerializer.Serialize<DiagnosticIssues>(issues);
        }

        private static string json2mre(Bundle messageBundle)
        {
            string MREString = null;

            try
            {
                DemographicsCodingMessage message = BaseMessage.Parse<BaseMessage>((Hl7.Fhir.Model.Bundle)messageBundle) as DemographicsCodingMessage;
                DeathRecord record = message.DeathRecord;
                IJEMortality ije = new IJEMortality(record, false);
                ije.DOD_YR = message.DeathYear.ToString();
                ije.DSTATE = message.JurisdictionId;
                ije.FILENO = message.StateAuxiliaryId.ToString();
                MREString = ije2mre(ije);
            }
            catch (Exception e)
            {
                Console.WriteLine($"*** Error parsing message: {e}");
                // Extraction errors require acks so we insert them in the DB to send with other messages to NCHS
                // Wrap this in another try catch so we can see any failures to create the extraction error in our logs
                //TBD
            }


            return MREString;
        }

        private static string json2trx(Bundle messageBundle)
        {
            string TRXString = null;

            try
            {
                CauseOfDeathCodingMessage message = BaseMessage.Parse<BaseMessage>((Hl7.Fhir.Model.Bundle)messageBundle) as CauseOfDeathCodingMessage;
                DeathRecord record = message.DeathRecord;
                IJEMortality ije = new IJEMortality(record, false);
                ije.DOD_YR = message.DeathYear.ToString();
                ije.DSTATE = message.JurisdictionId;
                ije.FILENO = message.StateAuxiliaryId.ToString();
                TRXString = ije2trx(ije);

            }
            catch (Exception e)
            {
                Console.WriteLine($"*** Error parsing message: {e}");
                // Extraction errors require acks so we insert them in the DB to send with other messages to NCHS
                // Wrap this in another try catch so we can see any failures to create the extraction error in our logs
                //TBD
            }


            return TRXString;
        }

        private static string ije2mre(IJEMortality ije)
        {
            string ijeString = ije.ToString();
            string mreString = string.Empty.PadRight(500);
            mreString = mreString.Insert(0, ije.DOD_YR);
            mreString = mreString.Insert(4, ije.DSTATE);
            mreString = mreString.Insert(6, ije.FILENO);
            mreString = mreString.Insert(15, ijeString.Substring(246, 324));
            mreString = mreString.Insert(342, ije.DETHNICE);
            mreString = mreString.Insert(345, ije.DETHNIC5C);
            return (mreString);
        }

        private static string ije2trx(IJEMortality ije)
        {
            string ijeString = ije.ToString();
            string trxString = string.Empty.PadRight(500);
            trxString = trxString.Insert(0, ije.DOD_YR);
            trxString = trxString.Insert(4, ije.DSTATE);
            trxString = trxString.Insert(6, ije.FILENO);
            trxString = trxString.Insert(21, ije.R_MO);
            trxString = trxString.Insert(23, ije.R_DY);
            trxString = trxString.Insert(25, ije.R_YR);
            trxString = trxString.Insert(41, ije.MANNER);
            trxString = trxString.Insert(42, ije.INT_REJ);
            trxString = trxString.Insert(43, ije.SYS_REJ);
            trxString = trxString.Insert(44, ije.INJPL);
            trxString = trxString.Insert(45, ije.MAN_UC);
            trxString = trxString.Insert(50, ije.ACME_UC);
            trxString = trxString.Insert(55, ije.EAC);
            trxString = trxString.Insert(215, ije.TRX_FLG);
            trxString = trxString.Insert(216, ije.RAC);
            trxString = trxString.Insert(316, ije.AUTOP);
            trxString = trxString.Insert(317, ije.AUTOPF);
            trxString = trxString.Insert(318, ije.TOBAC);
            trxString = trxString.Insert(319, ije.PREG);
            trxString = trxString.Insert(320, ije.PREG_BYPASS);
            trxString = trxString.Insert(321, ije.DOI_MO);
            trxString = trxString.Insert(323, ije.DOI_DY);
            trxString = trxString.Insert(325, ije.DOI_YR);
            trxString = trxString.Insert(329, ije.TOI_HR);
            trxString = trxString.Insert(333, ije.WORKINJ);
            trxString = trxString.Insert(334, ije.CERTL);
            trxString = trxString.Insert(364, ije.INACT);
            trxString = trxString.Insert(365, ije.AUXNO);
            trxString = trxString.Insert(377, ije.STATESP);
            return (trxString);
        }
    }
}