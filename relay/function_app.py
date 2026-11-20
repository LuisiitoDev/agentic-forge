"""Azure Functions entry point (Python v2 model). All logic is in forge_relay.core."""
import logging
import os

import azure.functions as func

from forge_relay import core

app = func.FunctionApp(http_auth_level=func.AuthLevel.ANONYMOUS)  # GitHub cannot send a function key; the HMAC is the auth


def _config():
    cfg = core.Config(
        webhook_secret=os.environ.get("RELAY_WEBHOOK_SECRET", ""),
        engine_repo=os.environ.get("RELAY_ENGINE_REPO", ""),
        dispatch_token=os.environ.get("RELAY_DISPATCH_TOKEN", ""),
        allowed_targets=os.environ.get("RELAY_ALLOWED_TARGETS", ""),
        bot_login=os.environ.get("RELAY_BOT_LOGIN", ""),
    )
    cfg.validate()
    return cfg


@app.route(route="webhook", methods=["POST"])
def webhook(req: func.HttpRequest) -> func.HttpResponse:
    try:
        cfg = _config()
    except ValueError:
        logging.error("relay is misconfigured")
        return func.HttpResponse("misconfigured", status_code=500)
    status, message = core.handle(dict(req.headers), req.get_body(), cfg)
    logging.info("delivery=%s status=%s %s", req.headers.get("x-github-delivery", ""), status, message)
    return func.HttpResponse(message, status_code=status)
