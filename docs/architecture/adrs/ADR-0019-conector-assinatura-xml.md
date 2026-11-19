# ADR-0019: Conector externo de assinatura XML

- **Status**: substituído; algoritmo customizado substituído pela chave do integrador (ADR-0020)
- **Substituído por**: ADR-0025 (o produto é só o microsserviço, 2026-11-16)
- **Data**: 2026-10-09
- **Fase**: 15

## Contexto

O usuário pediu uma implementação genérica de assinatura da DPS: escolher a
tag e assinar com um dos algoritmos disponíveis ou com um customizável.

Os limites obrigatórios do núcleo (CLAUDE.md e `.claude/rules/security.md`)
proíbem assinatura digital, criptografia e armazenamento de certificado no
motor. Essas responsabilidades são de conectores externos, e o motor oferece
pontos de extensão de pós-processamento.

## Decisão

1. **Projeto separado** `connectors/Tradux.Connectors.XmlSignature`, fora de
   `src/`. Nada do núcleo, da aplicação nem do SDK o referencia; o teste de
   arquitetura falha se `Tradux.Connectors.*` ou
   `System.Security.Cryptography.Xml` aparecerem em Domain, Xsd, Engine,
   Contracts, Application ou Sdk. Quem integra referencia o conector e o liga
   ao evento `XmlGenerated` do motor (ADR-0018), ou o usa direto no XML.
2. **`XmlSigner`**: assinatura XMLDSig envelopada, com as opções:
   - tag escolhida (`ElementName` e namespace opcional), referenciada pelo
     atributo `Id` (configurável), com opção de gerar o Id;
   - todas as ocorrências da tag (ex.: cada RPS de um lote) ou só a primeira;
   - documento inteiro (`WholeDocument`, `URI=""`), quando o layout não tem
     Id;
   - algoritmos: RSA-SHA1, RSA-SHA256 (padrão), RSA-SHA384, RSA-SHA512,
     ECDSA-SHA256;
   - algoritmo **customizado** (`ICustomSignatureAlgorithm`): URIs, hash e
     assinar/verificar delegados ao integrador (HSM, serviço remoto). É
     registrado como `SignatureDescription` sob a própria URI, sem reflexão
     em código interno;
   - canonicalização C14N (padrão) ou C14N exclusiva;
   - posição da assinatura: depois da tag (padrão), dentro dela ou no fim da
     raiz;
   - certificado no `KeyInfo`;
   - `Verify`, com o certificado embutido ou informado (inclusive para
     algoritmo customizado). **Sem certificado informado, só prova
     integridade**, não autoria: quem tiver o XML pode reassinar com outro
     certificado. Para autenticidade, informe o certificado esperado ou
     valide a cadeia. XML malformado, Id duplicado ou algoritmo desconhecido
     retornam `false`.
3. O conector **não guarda certificado nem chave**: recebe o
   `X509Certificate2` de quem chama. Espaços em branco são preservados,
   porque reformatar depois de assinar quebra a assinatura.

## Consequências

- A assinatura acontece depois da validação XSD, pelo evento `XmlGenerated`.
  Se o XSD de destino prevê a `Signature` (ex.: DPS), o resultado continua
  válido. Se não prevê (ex.: `webserviceNFSe.xsd`), assinar o documento
  inteiro é escolha do integrador.
- Transmissão, autenticação na prefeitura e guarda de certificado continuam
  fora do repositório, em conectores de emissão.
