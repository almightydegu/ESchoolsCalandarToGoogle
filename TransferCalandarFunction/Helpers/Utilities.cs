using System.Text;

namespace Br.ESchoolsCalandarToGoogle.Utilities
{
    public static class Utilities
    {
        public static string ConvertToGoogleId(string value)
        {
            return MikValSor.Encoding.Base32Encoder.Encode(Encoding.ASCII.GetBytes(value), MikValSor.Encoding.Base32Format.Base32Hex, false).ToLowerInvariant();
        }
    }
}