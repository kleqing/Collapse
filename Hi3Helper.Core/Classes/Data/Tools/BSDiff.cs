﻿// Original Source:
// https://raw.githubusercontent.com/LogosBible/bsdiff.net/master/src/bsdiff/BinaryPatchUtility.cs

using System;
using System.IO;
using System.IO.Compression;

namespace Hi3Helper.Data
{
    /*
	The original bsdiff.c source code (http://www.daemonology.net/bsdiff/) is
	distributed under the following license:

	Copyright 2003-2005 Colin Percival
	All rights reserved

	Redistribution and use in source and binary forms, with or without
	modification, are permitted providing that the following conditions 
	are met:
	1. Redistributions of source code must retain the above copyright
		notice, this list of conditions and the following disclaimer.
	2. Redistributions in binary form must reproduce the above copyright
		notice, this list of conditions and the following disclaimer in the
		documentation and/or other materials provided with the distribution.

	THIS SOFTWARE IS PROVIDED BY THE AUTHOR ``AS IS'' AND ANY EXPRESS OR
	IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
	WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
	ARE DISCLAIMED.  IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY
	DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
	DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS
	OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
	HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
	STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING
	IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
	POSSIBILITY OF SUCH DAMAGE.
	*/
    public sealed class BinaryPatchUtility
    {
        /// <summary>
        /// Applies a binary patch (in <a href="http://www.daemonology.net/bsdiff/">bsdiff</a> format) to the data in
        /// <paramref name="input"/> and writes the results of patching to <paramref name="output"/>.
        /// </summary>
        /// <param name="input">A <see cref="Stream"/> containing the input data.</param>
        /// <param name="openPatchStream">A func that can open a <see cref="Stream"/> positioned at the start of the patch data.
        /// This stream must support reading and seeking, and <paramref name="openPatchStream"/> must allow multiple streams on
        /// the patch to be opened concurrently.</param>
        /// <param name="output">A <see cref="Stream"/> to which the patched data is written.</param>
        public static void Apply(Stream input, Func<Stream> openPatchStream, Stream output)
        {
            // check arguments
            if (input == null)
                throw new ArgumentNullException("input");
            if (openPatchStream == null)
                throw new ArgumentNullException("openPatchStream");
            if (output == null)
                throw new ArgumentNullException("output");

            /*
			File format:
				0	8	"BSDIFF40"
				8	8	X
				16	8	Y
				24	8	sizeof(newfile)
				32	X	bzip2(control block)
				32+X	Y	bzip2(diff block)
				32+X+Y	???	bzip2(extra block)
			with control block a set of triples (x,y,z) meaning "add x bytes
			from oldfile to x bytes from the diff block; copy y bytes from the
			extra block; seek forwards in oldfile by z bytes".
			*/
            // read header
            long controlLength, diffLength, newSize;
            using (Stream patchStream = openPatchStream())
            {
                // check patch stream capabilities
                if (!patchStream.CanRead)
                    throw new ArgumentException("Patch stream must be readable.", "openPatchStream");
                if (!patchStream.CanSeek)
                    throw new ArgumentException("Patch stream must be seekable.", "openPatchStream");

                byte[] header = ReadExactly(patchStream, c_headerSize);

                // check for appropriate magic
                long signature = ReadInt64(header, 0);
                if (signature != c_fileSignature)
                    throw new InvalidOperationException("Corrupt patch.");

                // read lengths from header
                controlLength = ReadInt64(header, 8);
                diffLength = ReadInt64(header, 16);
                newSize = ReadInt64(header, 24);
                if (controlLength < 0 || diffLength < 0 || newSize < 0)
                    throw new InvalidOperationException("Corrupt patch.");
            }

            // preallocate buffers for reading and writing
            const int c_bufferSize = 1048576;
            byte[] newData = new byte[c_bufferSize];
            byte[] oldData = new byte[c_bufferSize];

            // prepare to read three parts of the patch in parallel
            using (Stream compressedControlStream = openPatchStream())
            using (Stream compressedDiffStream = openPatchStream())
            using (Stream compressedExtraStream = openPatchStream())
            {
                // seek to the start of each part
                compressedControlStream.Seek(c_headerSize, SeekOrigin.Current);
                compressedDiffStream.Seek(c_headerSize + controlLength, SeekOrigin.Current);
                compressedExtraStream.Seek(c_headerSize + controlLength + diffLength, SeekOrigin.Current);

                // decompress each part (to read it)
                using (GZipStream controlStream = new GZipStream(compressedControlStream, CompressionMode.Decompress))
                using (GZipStream diffStream = new GZipStream(compressedDiffStream, CompressionMode.Decompress))
                using (GZipStream extraStream = new GZipStream(compressedExtraStream, CompressionMode.Decompress))
                {
                    long[] control = new long[3];
                    byte[] buffer = new byte[8];

                    int oldPosition = 0;
                    int newPosition = 0;
                    while (newPosition < newSize)
                    {
                        // read control data
                        for (int i = 0; i < 3; i++)
                        {
                            ReadExactly(controlStream, buffer, 0, 8);
                            control[i] = ReadInt64(buffer, 0);
                        }

                        // sanity-check
                        if (newPosition + control[0] > newSize)
                            throw new InvalidOperationException("Corrupt patch.");

                        // seek old file to the position that the new data is diffed against
                        input.Position = oldPosition;

                        int bytesToCopy = (int)control[0];
                        while (bytesToCopy > 0)
                        {
                            int actualBytesToCopy = Math.Min(bytesToCopy, c_bufferSize);

                            // read diff string
                            ReadExactly(diffStream, newData, 0, actualBytesToCopy);

                            // add old data to diff string
                            int availableInputBytes = Math.Min(actualBytesToCopy, (int)(input.Length - input.Position));
                            ReadExactly(input, oldData, 0, availableInputBytes);

                            for (int index = 0; index < availableInputBytes; index++)
                                newData[index] += oldData[index];

                            output.Write(newData, 0, actualBytesToCopy);

                            // adjust counters
                            newPosition += actualBytesToCopy;
                            oldPosition += actualBytesToCopy;
                            bytesToCopy -= actualBytesToCopy;
                        }

                        // sanity-check
                        if (newPosition + control[1] > newSize)
                            throw new InvalidOperationException("Corrupt patch.");

                        // read extra string
                        bytesToCopy = (int)control[1];
                        while (bytesToCopy > 0)
                        {
                            int actualBytesToCopy = Math.Min(bytesToCopy, c_bufferSize);

                            ReadExactly(extraStream, newData, 0, actualBytesToCopy);
                            output.Write(newData, 0, actualBytesToCopy);

                            newPosition += actualBytesToCopy;
                            bytesToCopy -= actualBytesToCopy;
                        }

                        // adjust position
                        oldPosition = (int)(oldPosition + control[2]);
                    }
                }
            }
        }

        private static long ReadInt64(byte[] buf, int offset)
        {
            long value = buf[offset + 7] & 0x7F;

            for (int index = 6; index >= 0; index--)
            {
                value *= 256;
                value += buf[offset + index];
            }

            if ((buf[offset + 7] & 0x80) != 0)
                value = -value;

            return value;
        }

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes from <paramref name="stream"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="count">The count of bytes to read.</param>
        /// <returns>A new byte array containing the data read from the stream.</returns>
        private static byte[] ReadExactly(Stream stream, int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException("count");
            byte[] buffer = new byte[count];
            ReadExactly(stream, buffer, 0, count);
            return buffer;
        }

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes from <paramref name="stream"/> into
        /// <paramref name="buffer"/>, starting at the byte given by <paramref name="offset"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="buffer">The buffer to read data into.</param>
        /// <param name="offset">The offset within the buffer at which data is first written.</param>
        /// <param name="count">The count of bytes to read.</param>
        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            // check arguments
            if (stream == null)
                throw new ArgumentNullException("stream");
            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (offset < 0 || offset > buffer.Length)
                throw new ArgumentOutOfRangeException("offset");
            if (count < 0 || buffer.Length - offset < count)
                throw new ArgumentOutOfRangeException("count");

            while (count > 0)
            {
                // read data
                int bytesRead = stream.Read(buffer, offset, count);

                // check for failure to read
                if (bytesRead == 0)
                    throw new EndOfStreamException();

                // move to next block
                offset += bytesRead;
                count -= bytesRead;
            }
        }

        const long c_fileSignature = 0x3034464649445342L;
        const int c_headerSize = 32;
    }
}