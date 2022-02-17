﻿using System;
using Hl7.Fhir.Model;

namespace VRDR
{
    /// <summary>
    /// A <c>CodingResponseMessage</c> that conveys the coded demographic information of a decedent.
    /// </summary>
    public class DemographicCodingResponseMessage : CodingResponseMessage
    {
        /// <summary>
        /// The event URI for DemographicCodingResponseMessage.
        /// </summary>
        public const String MESSAGE_TYPE = "http://nchs.cdc.gov/vrdr_demographics_coding";

        /// <summary>Constructor that creates a response for the specified message.</summary>
        /// <param name="sourceMessage">the message to create a response for.</param>
        /// <param name="source">the endpoint identifier that the message will be sent from.</param>
        public DemographicCodingResponseMessage(BaseMessage sourceMessage, string source = "http://nchs.cdc.gov/vrdr_submission") : this(sourceMessage.MessageSource, source)
        {
            this.CertificateNumber = sourceMessage?.CertificateNumber;
            this.StateAuxiliaryIdentifier = sourceMessage?.StateAuxiliaryIdentifier;
            this.DeathJurisdictionID = sourceMessage?.DeathJurisdictionID;
            this.DeathYear = sourceMessage?.DeathYear;
        }

        /// <summary>
        /// Construct a DemographicCodingResponseMessage from a FHIR Bundle.
        /// </summary>
        /// <param name="messageBundle">a FHIR Bundle that will be used to initialize the DemographicCodingResponseMessage</param>
        /// <returns></returns>
        internal DemographicCodingResponseMessage(Bundle messageBundle) : base(messageBundle)
        {
        }

        /// <summary>Constructor that creates a response for the specified message.</summary>
        /// <param name="destination">the endpoint identifier that the response message will be sent to.</param>
        /// <param name="source">the endpoint identifier that the response message will be sent from.</param>
        public DemographicCodingResponseMessage(string destination, string source = "http://nchs.cdc.gov/vrdr_submission") : base(MESSAGE_TYPE, destination, source)
        {
        }
    }
}
