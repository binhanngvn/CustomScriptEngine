namespace System.Runtime.CompilerServices
{
    internal sealed class NullableAttribute : Attribute
    {
        public NullableAttribute(byte b) { }
        public NullableAttribute(byte[] b) { }
    }
    internal sealed class NullableContextAttribute : Attribute
    {
        public NullableContextAttribute(byte b) { }
    }
}