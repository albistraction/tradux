# Credenciais e chaves

## Papéis e permissões

`admin` (todas), `runtime` (só `template.use` — use para sistemas que só
geram XML), `editor` (`template.read|create|edit|test`), `auditor`
(`template.read`, `audit.read`). Cada credencial pertence a uma organização.

## Emitir credencial

Pelo SDK (não há rota HTTP de gestão de credenciais):

```csharp
var access = new TraduxAccess(new MongoApiCredentialStore(client, options), new MongoAuditSink(client, options));
var issued = await access.IssueAsync("org-a", "erp-producao", [Roles.Runtime], actor: "operador@empresa");
// issued.Secret aparece só aqui — entregue por canal seguro; não é guardado.
```

A emissão gera evento `permission.grant` na auditoria (sem o segredo).

## Revogar credencial (vazamento ou desligamento)

1. `access.ListAsync("org-a")` e localize o `Id` (o ator nas auditorias é
   `credential:<nome>#<8 primeiros do id>`).
2. `access.RevokeAsync("org-a", id, "operador@empresa")` — evento
   `permission.revoke`.
3. Efeito imediato na instância que revogou; nas demais, em até 30 s
   (cache de credencial).
4. Credencial inicial de configuração: revogue pelo SDK **e** retire da
   configuração — recolocá-la na configuração não a reativa.

## Credencial inicial (bootstrap)

Gere um segredo aleatório (`tradux_` + 43 caracteres), guarde o SHA-256
hexadecimal em `Tradux:Security:BootstrapCredentials:0:SecretSha256` e entregue
o segredo ao responsável. A API grava a credencial no primeiro startup.

## Rotação da chave da captura diagnóstica

1. Gere nova chave de 32 bytes (Base64) e troque `Tradux:Diagnostics:EncryptionKey`.
2. Reinicie as instâncias. Registros cifrados com a chave antiga ficam
   ilegíveis (são ignorados na leitura) e expiram sozinhos pelo TTL.
3. Se precisar dos antigos, leia-os antes de trocar a chave.
