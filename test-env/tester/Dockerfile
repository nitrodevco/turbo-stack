# Client-side tools for the end-to-end tests (kept off the "server" so it stays a fresh box).
FROM alpine:3.22

RUN apk add --no-cache bash curl jq oath-toolkit-oathtool openssl websocat openssh-client sshpass

COPY e2e/ /e2e/
ENTRYPOINT ["bash"]
