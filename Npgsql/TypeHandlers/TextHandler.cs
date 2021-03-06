﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text;
using Npgsql.BackendMessages;
using NpgsqlTypes;
using System.Data;

namespace Npgsql.TypeHandlers
{
    [TypeMapping("text",    NpgsqlDbType.Text, new[] { DbType.String, DbType.StringFixedLength }, new[] { typeof(string), typeof(char[]) })]
    [TypeMapping("varchar", NpgsqlDbType.Varchar)]
    [TypeMapping("bpchar",  NpgsqlDbType.Char, typeof(char))]
    [TypeMapping("name",    NpgsqlDbType.Name)]
    [TypeMapping("xml",     NpgsqlDbType.Xml, DbType.Xml)]
    [TypeMapping("unknown")]
    internal class TextHandler : TypeHandler<string>,
        IChunkingTypeWriter,
        IChunkingTypeReader<string>, IChunkingTypeReader<char[]>
    {
        public override bool PreferTextWrite { get { return true; } }

        #region State

        string _str;
        char[] _chars;
        byte[] _tempBuf;
        int _byteLen, _bytePos, _charPos;
        NpgsqlBuffer _buf;

        #endregion

        #region Read

        internal virtual void PrepareRead(NpgsqlBuffer buf, FieldDescription fieldDescription, int len)
        {
            _buf = buf;
            _byteLen = len;
            _bytePos = -1;
        }

        void IChunkingTypeReader<string>.PrepareRead(NpgsqlBuffer buf, FieldDescription fieldDescription, int len)
        {
            PrepareRead(buf, fieldDescription, len);
        }

        void IChunkingTypeReader<char[]>.PrepareRead(NpgsqlBuffer buf, FieldDescription fieldDescription, int len)
        {
            PrepareRead(buf, fieldDescription, len);
        }

        public bool Read(out string result)
        {
            if (_bytePos == -1)
            {
                if (_byteLen <= _buf.ReadBytesLeft)
                {
                    // Already have the entire string in the buffer, decode and return
                    result = _buf.ReadStringSimple(_byteLen);
                    _buf = null;
                    return true;
                }

                if (_byteLen <= _buf.Size) {
                    // Don't have the entire string in the buffer, but it can fit. Force a read to fill.
                    result = null;
                    return false;
                }

                // Bad case: the string doesn't fit in our buffer.
                // Allocate a temporary byte buffer to hold the entire string and read it in chunks.
                // TODO: Pool/recycle the buffer?
                _tempBuf = new byte[_byteLen];
                _bytePos = 0;
            }

            var len = Math.Min(_buf.ReadBytesLeft, _byteLen - _bytePos);
            _buf.ReadBytesSimple(_tempBuf, _bytePos, len);
            _bytePos += len;
            if (_bytePos < _byteLen)
            {
                result = null;
                return false;
            }

            result = _buf.TextEncoding.GetString(_tempBuf);
            _tempBuf = null;
            _buf = null;
            return true;
        }

        public bool Read(out char[] result)
        {
            if (_bytePos == -1)
            {
                if (_byteLen <= _buf.ReadBytesLeft)
                {
                    // Already have the entire string in the buffer, decode and return
                    result = _buf.ReadCharsSimple(_byteLen);
                    _buf = null;
                    return true;
                }

                if (_byteLen <= _buf.Size)
                {
                    // Don't have the entire string in the buffer, but it can fit. Force a read to fill.
                    result = null;
                    return false;
                }

                // Bad case: the string doesn't fit in our buffer.
                // Allocate a temporary byte buffer to hold the entire string and read it in chunks.
                // TODO: Pool/recycle the buffer?
                _tempBuf = new byte[_byteLen];
                _bytePos = 0;
            }

            var len = Math.Min(_buf.ReadBytesLeft, _byteLen - _bytePos);
            _buf.ReadBytesSimple(_tempBuf, _bytePos, len);
            _bytePos += len;
            if (_bytePos < _byteLen) {
                result = null;
                return false;
            }

            result = _buf.TextEncoding.GetChars(_tempBuf);
            _tempBuf = null;
            _buf = null;
            return true;
        }

        public long GetChars(DataRowMessage row, int charOffset, char[] output, int outputOffset, int charsCount, FieldDescription field)
        {
            if (row.PosInColumn == 0) {
                _charPos = 0;
            }

            if (output == null)
            {
                // Note: Getting the length of a text column means decoding the entire field,
                // very inefficient and also consumes the column in sequential mode. But this seems to
                // be SqlClient's behavior as well.
                int bytesSkipped, charsSkipped;
                row.Buffer.SkipChars(int.MaxValue, row.ColumnLen - row.PosInColumn, out bytesSkipped, out charsSkipped);
                Contract.Assume(bytesSkipped == row.ColumnLen - row.PosInColumn);
                row.PosInColumn += bytesSkipped;
                _charPos += charsSkipped;
                return _charPos;
            }

            if (charOffset < _charPos) {
                row.SeekInColumn(0);
                _charPos = 0;
            }

            if (charOffset > _charPos)
            {
                var charsToSkip = charOffset - _charPos;
                int bytesSkipped, charsSkipped;
                row.Buffer.SkipChars(charsToSkip, row.ColumnLen - row.PosInColumn, out bytesSkipped, out charsSkipped);
                row.PosInColumn += bytesSkipped;
                _charPos += charsSkipped;
                if (charsSkipped < charsToSkip) {
                    // TODO: What is the actual required behavior here?
                    throw new IndexOutOfRangeException();
                }
            }

            int bytesRead, charsRead;
            row.Buffer.ReadChars(output, outputOffset, charsCount, row.ColumnLen - row.PosInColumn, out bytesRead, out charsRead);
            row.PosInColumn += bytesRead;
            _charPos += charsRead;
            return charsRead;
        }
        
        #endregion

        #region Write

        public int ValidateAndGetLength(object value, ref LengthCache lengthCache)
        {
            if (lengthCache == null) { lengthCache = new LengthCache(1); }
            if (lengthCache.IsPopulated) { return lengthCache.Get(); }

            var asString = value as string;
            if (asString != null) {
                return lengthCache.Set(Encoding.UTF8.GetByteCount(asString));
            }

            var asCharArray = value as char[];
            if (asCharArray != null)
            {
                return lengthCache.Set(Encoding.UTF8.GetByteCount(asCharArray));                
            }

            throw new InvalidCastException("Can't write type as text: " + value.GetType());
        }

        public void PrepareWrite(object value, NpgsqlBuffer buf, LengthCache lengthCache)
        {
            _buf = buf;
            _charPos = -1;
            _byteLen = lengthCache.GetLast();

            _str = value as string;
            if (_str != null) { return; }

            _chars = value as char[];
            if (_chars != null) { return; }

            throw PGUtil.ThrowIfReached();
        }

        public bool Write(ref byte[] directBuf)
        {
            if (_charPos == -1)
            {
                if (_byteLen <= _buf.WriteSpaceLeft)
                {
                    // Can simply write the string to the buffer
                    if (_str != null)
                    {
                        _buf.WriteStringSimple(_str);
                        _str = null;
                    }
                    else
                    {
                        Contract.Assert(_chars != null);
                        _buf.WriteCharsSimple(_chars);
                        _str = null;                        
                    }
                    _buf = null;
                    return true;
                }

                if (_byteLen <= _buf.Size)
                {
                    // Buffer is currently too full, but the string can fit. Force a write to fill.
                    return false;
                }

                // Bad case: the string doesn't fit in our buffer.
                _charPos = 0;

                // For strings, allocate a temporary byte buffer to hold the entire string and write it directly.
                if (_str != null)
                {
                    directBuf = new byte[_byteLen];
                    _buf.TextEncoding.GetBytes(_str, 0, _str.Length, directBuf, 0);
                    return false;
                }
                Contract.Assert(_chars != null);

                // For char arrays, fall through to chunked writing below
            }

            if (_str != null)
            {
                // We did a direct buffer write above, and must now clean up
                _str = null;
                _buf = null;
                return true;
            }

            int charsUsed;
            bool completed;
            _buf.WriteStringChunked(_chars, _charPos, _chars.Length - _charPos, false, out charsUsed, out completed);
            if (completed)
            {
                // Flush encoder
                _buf.WriteStringChunked(_chars, _charPos, _chars.Length - _charPos, true, out charsUsed, out completed);
                _chars = null;
                _buf = null;
                return true;
            }
            _charPos += charsUsed;
            return false;
        }

        #endregion
    }
}
