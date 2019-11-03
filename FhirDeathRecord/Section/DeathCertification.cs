using System;
using System.Collections.Generic;
using System.Linq;
using FhirDeathRecord.Extensions;
using Hl7.Fhir.Model;

namespace FhirDeathRecord.Section
{
    /// <summary>Class <c>DeathCertification</c> models a Death Cerfication used in FHIR Vital Records Death Reporting (VRDR) Death
    /// Record. 
    /// </summary>
    public class DeathCertification
    {
        ///<summary>
        ///Get Reference url
        ///</summary>
        public string Url
        {
            get { return "urn:uuid:" + Resource.Id; }
        }

        /// <summary>Certified time.</summary>
        public string CertifiedTime
        {
            get
            {
                if (Resource?.Performed == null)
                {
                    return null;
                }

                return Resource.Performed.ToString();
            }
            set
            {
                Resource.Performed = new FhirDateTime(value);
            }
        }

        /// <summary>Certifier Role.</summary>
        public Dictionary<string, string> CertifierRole
        {
            get
            {
                var performerRole = Resource?.Performer?.FirstOrDefault()?.Role;
                return performerRole.ToDictionary();
            }
        }

        internal Procedure Resource { get; set; }

        ///<summary>Constructor</summary>
        public DeathCertification()
        {
            Resource = new Procedure
            {
                Id = Guid.NewGuid().ToString(),
                Meta = new Meta
                {
                    Profile = new List<string>() { "http://hl7.org/fhir/us/vrdr/StructureDefinition/VRDR-Death-Certification" }
                },
                Status = EventStatus.Completed,
                Category = new CodeableConcept("http://snomed.info/sct", "103693007", "Diagnostic procedure", null),
                Code = new CodeableConcept("http://snomed.info/sct", "308646001", "Death certification", null)
            };
        }

        public static DeathCertification CreateInstance(DeathRecord record = null)
        {
            var source = new DeathCertification();

            if (record != null)
            {
                record.Bundle.AddResourceEntry(source.Resource, source.Url);
                record.Composition.Section.First().Entry.Add(new ResourceReference(source.Url));
            }

            return source;
        }
    }
}