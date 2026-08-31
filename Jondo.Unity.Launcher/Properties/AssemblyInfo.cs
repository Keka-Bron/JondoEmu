using System.Runtime.CompilerServices;

// Para que las pruebas puedan mirar por dentro lo que no forma parte de la superficie publica del
// lanzador. Lo que mas importa aqui es SecretStore: es cifrado, y un cifrado sin pruebas es una
// promesa. Mismo apano que ya tenia Jondo.Unity.Server.
[assembly: InternalsVisibleTo("Jondo.Unity.Tests")]
