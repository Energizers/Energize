extern crate regex;
extern crate rand;

mod markov_chain;

use markov_chain::MarkovChain;

fn main()
{
    let path = String::from("markov/");
    let ext = String::from(".markov");
    let chain = MarkovChain::new(path,2,ext);
    let mut sentence = String::from("I like girls");

    chain.learn(sentence);
    println!("Hello, world!");
}