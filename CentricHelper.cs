using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CentricDataPull
{
    public static class CentricHelper
    {
        public static String HTMLFormatElement(string element)
        {
            return element.Replace("/", "%2F").Replace("|", "%7C");
        }
    }
}
