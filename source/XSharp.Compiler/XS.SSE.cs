﻿using Cosmos.Assembler.x86.SSE;
using static XSharp.Compiler.XSRegisters;

namespace XSharp.Compiler
{
  partial class XS
  {
    public static class SSE
    {
      public static void AddSS(RegisterXMM destination, RegisterXMM source)
      {
        DoDestinationSource<AddSS>(destination, source);
      }

      public static void MulSS(RegisterXMM destination, RegisterXMM source)
      {
        DoDestinationSource<MulSS>(destination, source);
      }

      public static void SubSS(RegisterXMM destination, RegisterXMM source)
      {
        DoDestinationSource<SubSS>(destination, source);
      }

      public static void XorPS(RegisterXMM destination, RegisterXMM source)
      {
        DoDestinationSource<XorPS>(destination, source);
      }

      public static void ConvertSI2SS(RegisterXMM destination, Register32 source)
      {
        new ConvertSI2SS()
        {
          DestinationReg = destination,
          SourceReg = source
        };
      }

      public static void ConvertSS2SIAndTruncate(Register32 destination, RegisterXMM source)
      {
        new ConvertSS2SIAndTruncate
        {
          DestinationReg = destination,
          SourceReg = source
        };
      }

      public static void DivPS(RegisterXMM destination, RegisterXMM source)
      {
        new DivPS
        {
          DestinationReg = destination,
          SourceReg = source
        };
      }

      public static void DivSS(RegisterXMM destination, RegisterXMM source)
      {
        new DivPS
        {
          DestinationReg = destination,
          SourceReg = source
        };
      }
    }
  }
}
