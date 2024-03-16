using System;
using System.Collections;
using System.Collections.Generic;

namespace VRDR.HTTP
{
    public class BundleResponse
    {

        public IList<Response>? messages { get; set; }

    }

    public class Response
    {


        public string messageId { get; set; }
        public string type { get; set; }
        public string reference { get; set; }
    }

    public class DiagnosticIssues
    {
        public string stateauxid { get; set; }
        public string description { get; set; }
    }
}

