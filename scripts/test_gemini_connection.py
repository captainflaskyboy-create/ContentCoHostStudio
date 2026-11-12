"""Make one small Gemini request without saving or printing the API key."""

import argparse
import getpass
import os
import sys


def main() -> int:
    # Windows terminals may default to a code page that cannot print model output.
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default="gemini-3.6-flash")
    parser.add_argument(
        "--api",
        choices=("interactions", "generate-content"),
        default="interactions",
        help="Use Interactions by default, or test the desktop app's generateContent API.",
    )
    args = parser.parse_args()

    try:
        from google import genai
        from google.genai import errors, types
    except ImportError:
        print("Install the SDK first: python -m pip install -U google-genai", file=sys.stderr)
        return 1

    key = os.environ.get("GEMINI_API_KEY")
    if not key:
        try:
            key = getpass.getpass("Gemini API key (hidden): ").strip()
        except (EOFError, KeyboardInterrupt):
            print("\nNo key supplied.", file=sys.stderr)
            return 1
    if not key.strip():
        print("Enter a Gemini API key or set GEMINI_API_KEY.", file=sys.stderr)
        return 1

    print(f"Testing {args.api} with {args.model} (one request, 30-second timeout).", flush=True)
    try:
        # Explicit credentials avoid a different GOOGLE_API_KEY taking precedence.
        with genai.Client(
            api_key=key.strip(),
            http_options=types.HttpOptions(
                timeout=30_000,
                retry_options=types.HttpRetryOptions(attempts=1),
            ),
        ) as client:
            prompt = "Explain how AI works in a few words."
            if args.api == "interactions":
                interaction = client.interactions.create(
                    model=args.model,
                    input=prompt,
                    store=False,
                )
                reply = interaction.output_text
            else:
                response = client.models.generate_content(model=args.model, contents=prompt)
                reply = response.text

        if not reply or not reply.strip():
            print("The API responded but returned no text.", file=sys.stderr)
            return 2
        print("Connection successful.")
        # Even an unexpected echoed credential must not appear in the output.
        print(reply.replace(key, "[redacted]").replace(key.strip(), "[redacted]"))
        return 0
    except errors.APIError as error:
        messages = {
            400: "Request rejected. Check the API key and model/API compatibility.",
            401: "Authentication failed. Check the API key.",
            403: "Access denied. Check the API key, project permissions, and region.",
            404: "The selected model or API endpoint is unavailable to this account.",
            429: "Rate limit or quota exceeded. Check your Gemini quota.",
            503: "Gemini is temporarily unavailable. Try again later.",
        }
        code = error.code if isinstance(error.code, int) else None
        print(f"API error {code}: {messages.get(code, 'The Gemini service could not complete the request.')}", file=sys.stderr)
        return 2
    except AttributeError:
        print("The installed SDK does not support this response/API shape. Upgrade google-genai.", file=sys.stderr)
        return 2
    except Exception as error:
        # Exception messages and tracebacks can contain request details. Print neither.
        if "timeout" in type(error).__name__.lower():
            print("No response arrived within the connection timeout. The service may be slow or unavailable; also check network access.", file=sys.stderr)
        else:
            print(f"Connection failed ({type(error).__name__}). Check DNS, internet access, proxy, and TLS settings.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
