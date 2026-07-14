using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace VRDR.Tests
{
    /// <summary>The profile tests mutate process-wide state (IJEMortality.ActiveProfile), so they
    /// must not run in parallel with other test classes that convert records.</summary>
    [CollectionDefinition("IJEProfileSerial", DisableParallelization = true)]
    public class IJEProfileSerialCollection { }

    [Collection("IJEProfileSerial")]
    public class IJEProfile_Should : IDisposable
    {
        public IJEProfile_Should()
        {
            IJEMortality.ActiveProfile = IJEMortality.Profiles.Standard;
        }

        public void Dispose()
        {
            IJEMortality.ActiveProfile = IJEMortality.Profiles.Standard;
        }

        [Fact]
        public void DefaultProfileIsStandardWith5000Length()
        {
            Assert.Equal(IJEMortality.Profiles.Standard, IJEMortality.ActiveProfile);
            Assert.Equal(5000, IJEMortality.RecordLength);
            IJEMortality ije = new IJEMortality(new DeathRecord(), false);
            Assert.Equal(5000, ije.ToString().Length);
        }

        [Fact]
        public void StandardProfileIncludesPlaceholdersExcludesWaFields()
        {
            List<string> names = IJEMortality.ActiveIJEProperties().Select(p => p.Name).ToList();
            Assert.Contains("PLACE1_1", names);
            Assert.Contains("PLACE20", names);
            Assert.Contains("BLANK2", names);
            Assert.Contains("BLANK3", names);
            Assert.DoesNotContain("INJRY_ADDR1", names);
            Assert.DoesNotContain("INJRY_ZIP9", names);
            Assert.DoesNotContain("INFO_GIVEN_NME", names);
            Assert.DoesNotContain("INFO_ZIP", names);
        }

        [Fact]
        public void WaProfileHas5614LengthAndWaFields()
        {
            IJEMortality.ActiveProfile = IJEMortality.Profiles.WA;
            Assert.Equal(5614, IJEMortality.RecordLength);
            IJEMortality ije = new IJEMortality(new DeathRecord(), false);
            Assert.Equal(5614, ije.ToString().Length);

            List<string> names = IJEMortality.ActiveIJEProperties().Select(p => p.Name).ToList();
            Assert.Contains("INJRY_ADDR1", names);
            Assert.Contains("INJRY_ZIP9", names);
            Assert.Contains("INFO_GIVEN_NME", names);
            Assert.Contains("INFO_ZIP", names);
            Assert.DoesNotContain("PLACE1_1", names);
            Assert.DoesNotContain("PLACE20", names);
            Assert.DoesNotContain("BLANK2", names);
            Assert.DoesNotContain("BLANK3", names);
        }

        [Fact]
        public void UnknownProfileIsRejected()
        {
            Assert.Throws<ArgumentException>(() => IJEMortality.ActiveProfile = "oregon");
            // and the active profile is unchanged by the failed set
            Assert.Equal(IJEMortality.Profiles.Standard, IJEMortality.ActiveProfile);
        }

        [Fact]
        public void ActiveFieldsNeverOverlapInEitherProfile()
        {
            foreach (string profile in new[] { IJEMortality.Profiles.Standard, IJEMortality.Profiles.WA })
            {
                IJEMortality.ActiveProfile = profile;
                var fields = IJEMortality.ActiveIJEProperties()
                    .Select(p => p.GetCustomAttribute<IJEField>())
                    .OrderBy(f => f.Location)
                    .ToList();
                for (int i = 1; i < fields.Count; i++)
                {
                    IJEField prev = fields[i - 1];
                    IJEField curr = fields[i];
                    Assert.True(prev.Location + prev.Length <= curr.Location,
                        $"Profile '{profile}': field {prev.Name} ({prev.Location}+{prev.Length}) overlaps {curr.Name} ({curr.Location})");
                }
                // no active field extends past the profile's record length
                IJEField last = fields.Last();
                Assert.True(last.Location - 1 + last.Length <= IJEMortality.RecordLength,
                    $"Profile '{profile}': field {last.Name} extends past record length {IJEMortality.RecordLength}");
            }
        }

        [Fact]
        public void WaFieldsRoundTripThroughIjeAndFhir()
        {
            IJEMortality.ActiveProfile = IJEMortality.Profiles.WA;
            IJEMortality ije = new IJEMortality();
            ije.INFO_GIVEN_NME = "Joe";
            ije.INFO_LST_NME = "Smith";
            ije.INFO_ADDR_ONE_LINE = "100 W Kyle St Washington 98010";
            // NOTE: INFO_CITY/INFO_STATE/INFO_ZIP are one-way by WA design: their DeathRecord
            // setters are no-ops (informant address persists via INFO_ADDR_ONE_LINE only); the
            // getters read address components when a FHIR message arrives with them populated.
            ije.INJRY_ADDR1 = "123 Injury Ln";
            ije.INJRY_ZIP9 = "98011";

            string s = ije.ToString();
            Assert.Equal(5614, s.Length);
            // spot-check the fixed positions (1-based locations 4430, 4558, 4568, 4868)
            Assert.Equal("123 Injury Ln", s.Substring(4429, 128).TrimEnd());
            Assert.Equal("98011", s.Substring(4557, 10).TrimEnd());
            Assert.Equal("Joe", s.Substring(4567, 200).TrimEnd());
            Assert.Equal("100 W Kyle St Washington 98010", s.Substring(4867, 512).TrimEnd());

            // parse back and confirm both the IJE getters and the FHIR-side properties
            IJEMortality parsed = new IJEMortality(s, false);
            Assert.Equal("Joe", parsed.INFO_GIVEN_NME.TrimEnd());
            Assert.Equal("Smith", parsed.INFO_LST_NME.TrimEnd());
            Assert.Equal("100 W Kyle St Washington 98010", parsed.INFO_ADDR_ONE_LINE.TrimEnd());
            Assert.Equal("123 Injury Ln", parsed.INJRY_ADDR1.TrimEnd());
            DeathRecord record = parsed.ToDeathRecord();
            Assert.Equal("Joe", record.InformantGivenName);
            Assert.Equal("Smith", record.InformantFamilyName);
        }

        [Fact]
        public void RelationshipAndInformantNameComposeIntoOneContact()
        {
            IJEMortality.ActiveProfile = IJEMortality.Profiles.WA;
            IJEMortality ije = new IJEMortality();
            // name first, then relationship: with the old add-a-new-entry setter the relationship
            // would land in a second Patient.contact entry and this getter pair could not both succeed
            ije.INFO_GIVEN_NME = "Joe";
            ije.INFORMRELATE = "sibling";
            DeathRecord record = ije.ToDeathRecord();
            Assert.Equal("Joe", record.InformantGivenName);
            Assert.Equal("sibling", record.ContactRelationship["text"]);
        }

        [Fact]
        public void StandardProfileToleratesWaLengthInput()
        {
            string ije5614 = new IJEMortality(new DeathRecord(), false).ToString().PadRight(5614, ' ');
            IJEMortality parsed = new IJEMortality(ije5614, false); // tail past 5000 is ignored
            Assert.Equal(5000, parsed.ToString().Length);
        }

        [Fact]
        public void WaProfileToleratesStandardLengthInput()
        {
            IJEMortality.ActiveProfile = IJEMortality.Profiles.WA;
            string ije5000 = new string(' ', 5000);
            IJEMortality parsed = new IJEMortality(ije5000, false); // padded to 5614, WA fields blank
            Assert.Equal("", parsed.INFO_GIVEN_NME.Trim());
            Assert.Equal(5614, parsed.ToString().Length);
        }

        [Fact]
        public void WaDataDoesNotLeakIntoStandardOutput()
        {
            // record carries informant data (e.g. arrived via FHIR from WA), but a standard-profile
            // conversion must leave the PLACE/BLANK placeholder area untouched by it
            DeathRecord record = new DeathRecord();
            record.InformantGivenName = "Joe";
            record.InformantFamilyName = "Smith";
            IJEMortality ije = new IJEMortality(record, false);
            string s = ije.ToString();
            Assert.Equal(5000, s.Length);
            Assert.Equal("", s.Substring(4429, 571).Trim()); // 4430..5000 placeholder area all blank
        }
    }
}
